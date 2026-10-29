using System.Text;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using Sync.Legacy;
using WorkOrders.Api.Sync;

namespace Sync.Tests;

/// <summary>The property the design rests on: whatever order the messages arrive in, and however many times each
/// is delivered, the new side ends up equal to the legacy side.</summary>
[Collection("stack")]
public class ConvergenceTests(PostgresFixture pg, SqlServerFixture sql) : IAsyncLifetime
{
    public Task InitializeAsync() => sql.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Shuffled_and_duplicated_delivery_converges_to_the_legacy_state()
    {
        // A realistic life of one order: desktop creates, releases, records both operations and scraps;
        // the web planner changes the plan and scraps; the desktop scraps again and closes.
        await sql.DesktopAsync("DECLARE @id int; EXEC dbo.uspWorkOrderCreate 514, 10, '2026-12-01', 27, @id OUTPUT;");
        var id = await sql.ScalarAsync<int>("SELECT MAX(WorkOrderID) FROM Production.WorkOrder");
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderRelease {id}, 27");
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 1, 7, 27");
        var handler = new LegacyCommandHandler(sql.AppConnectionString);
        await handler.HandleAsync(new Message("new-1", Topology.ChangePlan, id, 0, "new",
            Encoding.UTF8.GetBytes("""{"orderQty":12,"dueDate":"2026-12-20T00:00:00","actorBusinessEntityId":40}""")));
        await handler.HandleAsync(new Message("new-2", Topology.RecordScrap, id, 0, "new",
            Encoding.UTF8.GetBytes("""{"quantity":2,"reasonId":8,"actorBusinessEntityId":40}""")));
        foreach (var op in await sql.QueryAsync($"SELECT OperationSequence FROM Production.WorkOrderRouting WHERE WorkOrderID = {id}"))
            await sql.DesktopAsync($"EXEC dbo.uspWorkOrderRoutingRecord {id}, {op[0]}, '2026-12-02 08:00', '2026-12-02 12:00', 4.0, 55.5, 27");
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 1, 9, 27");
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderClose {id}, 27");

        var rows = await sql.QueryAsync($"SELECT OutboxId, Version, Origin, Payload FROM sync.Outbox WHERE AggregateId = {id} ORDER BY OutboxId");
        Assert.True(rows.Count >= 8, $"expected a rich history, got {rows.Count} messages");
        var messages = rows.Select(r => new Message($"legacy-{r[0]}",
            (string)r[2]! == "new" ? Topology.WorkOrderConfirmed : Topology.WorkOrderChanged,
            id, Convert.ToInt64(r[1]), (string)r[2]!, Encoding.UTF8.GetBytes((string)r[3]!))).ToList();

        var legacyOrder = (await sql.QueryAsync($"SELECT OrderQty, ScrappedQty, DueDate, EndDate FROM Production.WorkOrder WHERE WorkOrderID = {id}")).Single();
        var legacyOps = await sql.QueryAsync($"SELECT OperationSequence, ActualStartDate, ActualEndDate, ActualCost FROM Production.WorkOrderRouting WHERE WorkOrderID = {id} ORDER BY OperationSequence");

        for (var seed = 0; seed < 25; seed++)
        {
            var rnd = new Random(seed);
            var delivery = messages.Concat(messages.Where(_ => rnd.Next(3) == 0)).Concat(messages.Where(_ => rnd.Next(3) == 0))
                .OrderBy(_ => rnd.Next()).ToList();
            await using (var clean = await pg.CleanContextAsync()) { }

            foreach (var m in delivery)
            {
                await using var db = pg.NewContext();
                await new WorkOrderSyncHandler(db).HandleAsync(m);
            }

            await using var check = pg.NewContext();
            var wo = await check.WorkOrders.Include(w => w.Operations).SingleAsync();
            var ctx = $"seed {seed}";
            Assert.True((int)legacyOrder[0]! == wo.OrderQty, $"{ctx}: quantity");
            Assert.True(Convert.ToInt32(legacyOrder[1]) == wo.ScrappedQty, $"{ctx}: scrap");
            Assert.True((DateTime)legacyOrder[2]! == wo.DueDate, $"{ctx}: due date");
            Assert.True(wo.EndDate != null, $"{ctx}: closed");
            Assert.Equal(legacyOps.Count, wo.Operations.Count);
            foreach (var (l, n) in legacyOps.Zip(wo.Operations.OrderBy(o => o.OperationSequence)))
            {
                Assert.True((DateTime)l[1]! == n.ActualStartDate, $"{ctx}: op actual start");
                Assert.True((DateTime)l[2]! == n.ActualEndDate, $"{ctx}: op actual end");
                Assert.True((decimal)l[3]! == n.ActualCost, $"{ctx}: op cost");
            }
            Assert.Equal(messages.Count, await check.ProcessedMessages.CountAsync());   // each distinct message recorded once
        }
    }
}
