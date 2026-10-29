using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Erasure;
using WorkOrders.Api.Import;

namespace Sync.Tests;

/// <summary>Erasing a person (an AdventureWorks employee who acted as planner) across both databases and the broker.
/// Every claim is checked by the same scan that first proves it can find the person.</summary>
[Collection("stack")]
public class ErasureTests(PostgresFixture pg, SqlServerFixture sql, RabbitFixture rabbit) : IAsyncLifetime
{
    private const int Subject = 78;   // Reuben D'sa, production supervisor: identifiers unique in the sample, and an apostrophe to exercise escaping
    private const int Colleague = 40; // JoLynn Dobney

    public async Task InitializeAsync()
    {
        await rabbit.ResetAsync();
        await sql.ResetAsync(patchTrigger: false);   // Microsoft's trigger as shipped, so a quantity change can be made to dead-letter
        await using var db = await pg.CleanContextAsync();
        await new LegacyImporter(db, sql.AppConnectionString, [Subject, Colleague]).RunAsync(30);   // orders, scrap reasons, planners with their labels
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ErasureVerifier Verifier(TestSystem system) => new(pg.ConnectionString, sql.AppConnectionString, system.Bus);

    private async Task<int> DesktopCreate(int actor, int qty = 20)
    {
        await sql.DesktopAsync($"DECLARE @id int; EXEC dbo.uspWorkOrderCreate 514, {qty}, '2027-01-15', {actor}, @id OUTPUT;");
        return await sql.ScalarAsync<int>("SELECT MAX(WorkOrderID) FROM Production.WorkOrder");
    }

    private async Task<Guid> NewSideId(int legacyId)
    {
        await using var db = pg.NewContext();
        return (await db.WorkOrders.SingleAsync(w => w.LegacyWorkOrderId == legacyId)).Id;
    }

    /// <summary>A realistic week for the subject: desktop and web edits, and one command stuck in the dead-letter queue.</summary>
    private async Task WorkAsSubject(TestSystem system)
    {
        var a = await DesktopCreate(Subject);
        var b = await DesktopCreate(Colleague);
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {a}, 1, 7, {Subject}");
        await system.DrainAsync();
        await system.AsPlanner(c => c.ScrapAsync(Subject, NewSideId(a).Result, 2, 8));
        await system.AsPlanner(c => c.ScrapAsync(Colleague, NewSideId(b).Result, 1, 8));
        await system.DrainAsync();
        // Microsoft's trigger refuses quantity changes, so this command ends in the dead-letter queue still naming the subject.
        await system.AsPlanner(c => c.ChangePlanAsync(Subject, NewSideId(a).Result, 30, null));
        await Eventually(async () => await rabbit.CountAsync(Topology.LegacyDeadLetterQueue) == 1, "the stuck command");
        await system.DrainAsync();
    }

    [Fact]
    public async Task Erasing_a_person_leaves_no_copy_in_either_database_or_the_broker()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        await WorkAsSubject(system);
        var needles = await ErasureService.CollectNeedlesAsync(sql.AppConnectionString, Subject);
        var verifier = Verifier(system);
        var before = await verifier.ScanAsync(needles, Subject);

        // The search must be able to find them before it is trusted to find nothing: every kind of identifier we
        // collected must be found somewhere first.
        var kinds = needles.Select(n => n.Kind).ToHashSet();
        Assert.Contains("full name", kinds); Assert.Contains("email address", kinds); Assert.Contains("national id", kinds);
        foreach (var kind in kinds) Assert.Contains(before, h => h.Kind == kind);
        Assert.Contains(before, h => h.Store == "postgres" && h.Where == "planner.display_name" && h.Kind == "full name");
        Assert.Contains(before, h => h.Store == "postgres" && h.Where.StartsWith("outbox") && h.Kind == "actor reference");
        Assert.Contains(before, h => h.Store == "sqlserver" && h.Where == "Person.EmailAddress" && h.Kind == "email address");
        Assert.Contains(before, h => h.Store == "sqlserver" && h.Where == "HumanResources.Employee" && h.Kind == "national id");
        Assert.Contains(before, h => h.Store == "sqlserver" && h.Where.StartsWith("dbo.WorkOrderAudit") && h.Kind == "actor reference");
        Assert.Contains(before, h => h.Store == "broker" && h.Where == Topology.LegacyDeadLetterQueue && h.Kind == "actor reference");

        var result = await new ErasureService(pg.NewContext(), system.Bus).EraseAsync(Subject);

        var after = await verifier.ScanAsync(needles, Subject);
        Assert.True(after.Count == 0, "left behind: " + string.Join("; ", after.Select(h => $"{h.Store} {h.Where} ({h.Kind}) x{h.Count}")));
        Assert.True(result.QueueMessagesRedacted >= 1);   // the dead-lettered command

        // The legacy side: anonymised, not deleted, because other rows point at them.
        Assert.Equal("Erased", await sql.ScalarAsync<string>($"SELECT LastName FROM Person.Person WHERE BusinessEntityID = {Subject}"));
        Assert.Equal(1, await sql.ScalarAsync<int>($"SELECT COUNT(*) FROM HumanResources.Employee WHERE BusinessEntityID = {Subject}"));
        Assert.Equal(1, await sql.ScalarAsync<int>($"SELECT COUNT(*) FROM HumanResources.EmployeePayHistory WHERE BusinessEntityID = {Subject}"));   // retained: a legal question, documented
        Assert.Equal(0, await sql.ScalarAsync<int>($"SELECT COUNT(*) FROM Person.EmailAddress WHERE BusinessEntityID = {Subject}"));
        Assert.True(await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkOrderAudit WHERE ActorBusinessEntityID = -1") >= 3);
        Assert.NotEqual("Erased", await sql.ScalarAsync<string>($"SELECT LastName FROM Person.Person WHERE BusinessEntityID = {Colleague}"));

        // Evidence on both sides, with no personal data in it.
        var legacyLog = (await sql.QueryAsync("SELECT RequestId, SubjectBusinessEntityID, Detail FROM dbo.ErasureLog")).Single();
        Assert.Equal(Subject, Convert.ToInt32(legacyLog[1]));
        await using var db = pg.NewContext();
        var newLog = await db.ErasureLog.SingleAsync();
        Assert.Equal((Guid)legacyLog[0]!, newLog.RequestId);
        Assert.NotNull(newLog.LegacyConfirmedAt);
        Assert.NotNull(newLog.NewPurgedAt);
        foreach (var n in needles) { Assert.DoesNotContain(n.Value, (string)legacyLog[2]!, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(n.Value, newLog.Detail!, StringComparison.OrdinalIgnoreCase); }

        // The new side: label gone, cannot sign in; the colleague is unaffected.
        var planner = await db.Planners.SingleAsync(p => p.BusinessEntityId == Subject);
        Assert.Null(planner.DisplayName);
        Assert.NotNull(planner.ErasedAt);
        Assert.NotNull((await db.Planners.SingleAsync(p => p.BusinessEntityId == Colleague)).DisplayName);

        // Asking again changes nothing.
        var again = await new ErasureService(pg.NewContext(), system.Bus).EraseAsync(Subject);
        Assert.StartsWith("already erased", again.Detail);
        Assert.Equal(1, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ErasureLog"));
    }

    [Fact]
    public async Task A_dead_lettered_command_that_named_the_person_still_works_after_redaction_and_brings_the_name_back_nowhere()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        await WorkAsSubject(system);
        var needles = await ErasureService.CollectNeedlesAsync(sql.AppConnectionString, Subject);
        await new ErasureService(pg.NewContext(), system.Bus).EraseAsync(Subject);

        await sql.ApplyScriptAsync("V006");   // ship the trigger fix, then put the stuck command back
        Assert.Equal(1, await system.Bus.RedriveAsync(Topology.LegacyDeadLetterQueue));
        await system.DrainAsync();

        var order = await sql.ScalarAsync<int>("SELECT TOP 1 WorkOrderID FROM dbo.WorkOrderAudit WHERE Action = 'ChangePlan' ORDER BY AuditID DESC");
        Assert.Equal(30, await sql.ScalarAsync<int>($"SELECT OrderQty FROM Production.WorkOrder WHERE WorkOrderID = {order}"));   // the command ran
        Assert.Equal(-1, await sql.ScalarAsync<int>($"SELECT ActorBusinessEntityID FROM dbo.WorkOrderAudit WHERE WorkOrderID = {order} AND Action = 'ChangePlan'"));
        Assert.Empty(await Verifier(system).ScanAsync(needles, Subject));
    }

    [Fact]
    public async Task A_late_write_that_names_an_erased_person_is_stored_without_them()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        var id = await DesktopCreate(Colleague);
        await system.DrainAsync();
        await new ErasureService(pg.NewContext(), system.Bus).EraseAsync(Subject);

        // Something still in flight, or a script, names them after the erasure.
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 1, 7, {Subject}");

        Assert.Equal(-1, await sql.ScalarAsync<int>($"SELECT TOP 1 ActorBusinessEntityID FROM dbo.WorkOrderAudit WHERE WorkOrderID = {id} AND Action = 'Scrap'"));
        Assert.Equal(0, await sql.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.WorkOrderAudit WHERE ActorBusinessEntityID = {Subject}"));
    }

    [Fact]
    public async Task The_new_side_and_the_broker_never_receive_names_and_telemetry_never_names_the_actor()
    {
        var tags = new List<(string Key, string Value)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (tags) foreach (var t in a.TagObjects) tags.Add((t.Key, t.Value?.ToString() ?? "")); },
        };
        ActivitySource.AddActivityListener(listener);

        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        await WorkAsSubject(system);
        var names = new[] { "Reuben D'sa", "JoLynn Dobney" }.Select(n => new Needle("full name", n)).ToList();

        var hits = await Verifier(system).ScanAsync(names, subject: -999);   // -999: no actor-id check here, names only
        // The only copy of a name on the new side is the planner label the UI needs; none in messages, payloads or anywhere else.
        Assert.All(hits, h => Assert.Equal(("postgres", "planner.display_name"), (h.Store, h.Where)));
        Assert.Equal(2, hits.Count);

        lock (tags)
        {
            Assert.NotEmpty(tags);
            Assert.DoesNotContain(tags, t => t.Key.Contains("actor", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(tags, t => t.Value.Contains("D'sa") || t.Value.Contains("Dobney") || t.Value.Contains("adventure-works"));
        }
    }

    private static async Task Eventually(Func<Task<bool>> cond, string what, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (await cond()) return; await Task.Delay(200); }
        Assert.Fail("timed out waiting for " + what);
    }
}
