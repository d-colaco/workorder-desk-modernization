using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Sync.Contracts;
using Sync.Legacy;

namespace Sync.Tests;

/// <summary>Failure cases on the legacy side, against a real SQL Server with AdventureWorks restored fresh for each test.</summary>
[Collection("stack")]
public class LegacyCommandHandlerTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const int Actor = 27;
    private const int Product = 514;   // has routing in AdventureWorks history
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync() => sql.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private LegacyCommandHandler Handler() => new(sql.AppConnectionString);

    private static Message Cmd(string id, string type, long legacyId, object body, string? newId = null) =>
        new(id, type, legacyId, 0, "new", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json)), NewId: newId);

    private static Message Create(string id, Guid newId, int qty = 5) =>
        Cmd(id, Topology.CreateWorkOrder, 0, new CreateWorkOrderCommand(Product, qty, new DateTime(2026, 12, 1), Actor), newId.ToString());

    private Task<int> OrderQty(long id) => sql.ScalarAsync<int>("SELECT OrderQty FROM Production.WorkOrder WHERE WorkOrderID = @id", ("@id", id));
    private async Task<int> NewestOrderId() => await sql.ScalarAsync<int>("SELECT MAX(WorkOrderID) FROM Production.WorkOrder");

    [Fact]
    public async Task Create_runs_the_procedure_and_the_outbox_row_says_origin_new_with_our_id()
    {
        var newId = Guid.CreateVersion7();
        Assert.Equal(CommandResult.Applied, await Handler().HandleAsync(Create("new-1", newId)));

        var id = await NewestOrderId();
        var row = (await sql.QueryAsync($"SELECT Origin, NewId, Version FROM sync.Outbox WHERE AggregateId = {id}")).Single();
        Assert.Equal("new", row[0]);
        Assert.Equal(newId, row[1]);
        Assert.Equal(1, Convert.ToInt32(row[2]));
        Assert.Equal(Actor, await sql.ScalarAsync<int>($"SELECT ActorBusinessEntityID FROM dbo.WorkOrderAudit WHERE WorkOrderID = {id}"));
        Assert.Equal(1, await sql.ScalarAsync<int>($"SELECT COUNT(*) FROM Production.TransactionHistory WHERE ReferenceOrderID = {id} AND TransactionType = 'W'"));
    }

    [Fact]
    public async Task Duplicate_command_is_applied_once()
    {
        var m = Create("new-1", Guid.CreateVersion7());
        var before = await sql.ScalarAsync<int>("SELECT COUNT(*) FROM Production.WorkOrder");

        Assert.Equal(CommandResult.Applied, await Handler().HandleAsync(m));
        Assert.Equal(CommandResult.Duplicate, await Handler().HandleAsync(m));
        Assert.Equal(CommandResult.Duplicate, await Handler().HandleAsync(m));

        Assert.Equal(before + 1, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM Production.WorkOrder"));
        Assert.Equal(1, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Inbox"));
    }

    [Fact]
    public async Task Concurrent_duplicates_apply_once()
    {
        var m = Create("new-1", Guid.CreateVersion7());
        var before = await sql.ScalarAsync<int>("SELECT COUNT(*) FROM Production.WorkOrder");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try { return await Handler().HandleAsync(m); }
            catch (SqlException ex) when (ex.Number == 1205) { return CommandResult.Duplicate; }   // deadlock victim: the message would be retried and then be a duplicate
        })));

        Assert.Equal(1, results.Count(r => r == CommandResult.Applied));
        Assert.Equal(before + 1, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM Production.WorkOrder"));
    }

    [Fact]
    public async Task Trigger_rollback_fails_the_whole_command_and_leaves_no_trace_so_a_retry_is_possible()
    {
        await sql.ResetAsync(patchTrigger: false);   // Microsoft's uWorkOrder as shipped
        await Handler().HandleAsync(Create("new-1", Guid.CreateVersion7(), qty: 5));
        var id = await NewestOrderId();
        var auditBefore = await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkOrderAudit");
        var outboxBefore = await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Outbox");

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            Handler().HandleAsync(Cmd("new-2", Topology.ChangePlan, id, new ChangePlanCommand(9, null, Actor))));

        Assert.Equal(3609, ex.Number);   // "transaction ended in the trigger"; the original error 515 is only printed
        Assert.Equal(5, await OrderQty(id));
        Assert.Equal(auditBefore, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkOrderAudit"));
        Assert.Equal(outboxBefore, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Outbox"));
        Assert.Equal(0, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Inbox WHERE MessageId = 'new-2'"));   // not recorded, so redelivery retries it
    }

    [Fact]
    public async Task With_the_lab_patch_the_same_quantity_change_succeeds()
    {
        await Handler().HandleAsync(Create("new-1", Guid.CreateVersion7(), qty: 5));
        var id = await NewestOrderId();

        await Handler().HandleAsync(Cmd("new-2", Topology.ChangePlan, id, new ChangePlanCommand(9, null, Actor)));

        Assert.Equal(9, await OrderQty(id));
    }

    [Fact]
    public async Task The_origin_marker_does_not_leak_to_the_next_user_of_a_pooled_connection()
    {
        await Handler().HandleAsync(Create("new-1", Guid.CreateVersion7()));
        var id = await NewestOrderId();
        await Handler().HandleAsync(Cmd("new-2", Topology.RecordScrap, id, new RecordScrapCommand(1, 7, Actor)));

        // Same connection string, hence the same pool: the desktop app's next write must not look like ours.
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 1, 7, {Actor}");

        var origins = (await sql.QueryAsync($"SELECT Origin FROM sync.Outbox WHERE AggregateId = {id} ORDER BY OutboxId")).Select(r => (string)r[0]!).ToList();
        Assert.Equal(["new", "new", "legacy"], origins);   // create, scrap command, then the desktop scrap
    }

    [Fact]
    public async Task Scrap_entered_on_both_sides_adds_up()
    {
        await Handler().HandleAsync(Create("new-1", Guid.CreateVersion7(), qty: 10));
        var id = await NewestOrderId();

        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 3, 7, {Actor}");                                  // the floor, on the desktop app
        await Handler().HandleAsync(Cmd("new-2", Topology.RecordScrap, id, new RecordScrapCommand(2, 8, Actor)));   // the planner, on the web

        Assert.Equal(5, await sql.ScalarAsync<int>($"SELECT ScrappedQty FROM Production.WorkOrder WHERE WorkOrderID = {id}"));
    }

    [Fact]
    public async Task Scrap_commands_give_the_same_total_in_either_order()
    {
        await Handler().HandleAsync(Create("new-1", Guid.CreateVersion7(), qty: 10));
        await Handler().HandleAsync(Create("new-2", Guid.CreateVersion7(), qty: 10));
        var b = await NewestOrderId(); var a = b - 1;
        Message S(string id, long order, int q) => Cmd(id, Topology.RecordScrap, order, new RecordScrapCommand(q, 7, Actor));

        await Handler().HandleAsync(S("s1", a, 1)); await Handler().HandleAsync(S("s2", a, 4));
        await Handler().HandleAsync(S("s4", b, 4)); await Handler().HandleAsync(S("s3", b, 1));

        Assert.Equal(5, await sql.ScalarAsync<int>($"SELECT ScrappedQty FROM Production.WorkOrder WHERE WorkOrderID = {a}"));
        Assert.Equal(5, await sql.ScalarAsync<int>($"SELECT ScrappedQty FROM Production.WorkOrder WHERE WorkOrderID = {b}"));
    }

    [Fact]
    public async Task Both_sides_editing_the_due_date_ends_with_the_later_write_and_both_changes_are_in_the_outbox()
    {
        await Handler().HandleAsync(Create("new-1", Guid.CreateVersion7()));
        var id = await NewestOrderId();

        await sql.ExecAsync($"UPDATE Production.WorkOrder SET DueDate = '2026-11-01' WHERE WorkOrderID = {id}");   // another legacy screen
        await Handler().HandleAsync(Cmd("new-2", Topology.ChangePlan, id, new ChangePlanCommand(null, new DateTime(2026, 12, 20), Actor)));

        Assert.Equal(new DateTime(2026, 12, 20), await sql.ScalarAsync<DateTime>($"SELECT DueDate FROM Production.WorkOrder WHERE WorkOrderID = {id}"));
        var origins = (await sql.QueryAsync($"SELECT Origin FROM sync.Outbox WHERE AggregateId = {id} ORDER BY Version")).Select(r => (string)r[0]!).ToList();
        Assert.Equal(["new", "legacy", "new"], origins);   // so a conflict is detectable and loggable (L6)
    }

    [Fact]
    public async Task A_command_for_an_order_closed_meanwhile_is_refused_logged_and_answered_with_the_current_state()
    {
        await Handler().HandleAsync(Create("new-1", Guid.CreateVersion7()));
        var id = await NewestOrderId();
        await sql.ExecAsync($"UPDATE Production.WorkOrder SET EndDate = GETDATE() WHERE WorkOrderID = {id}");   // closed on the desktop meanwhile

        var result = await Handler().HandleAsync(Cmd("new-2", Topology.RecordScrap, id, new RecordScrapCommand(1, 7, Actor)));

        Assert.Equal(CommandResult.Rejected, result);   // a business refusal: acknowledged, never retried
        Assert.Equal(0, await sql.ScalarAsync<int>($"SELECT ScrappedQty FROM Production.WorkOrder WHERE WorkOrderID = {id}"));
        var conflict = (await sql.QueryAsync($"SELECT Field, Winner, MessageId FROM sync.ConflictLog WHERE WorkOrderID = {id}")).Single();
        Assert.Equal(["Closed", "legacy", "new-2"], conflict.Select(c => (string)c!));
        var answer = (await sql.QueryAsync($"SELECT TOP 1 Origin, CommandId FROM sync.Outbox WHERE AggregateId = {id} ORDER BY OutboxId DESC")).Single();
        Assert.Equal(["new", "new-2"], answer.Select(c => (string)c!));   // the new side learns the truth and closes the command
        Assert.Equal(CommandResult.Duplicate, await Handler().HandleAsync(Cmd("new-2", Topology.RecordScrap, id, new RecordScrapCommand(1, 7, Actor))));
    }
}
