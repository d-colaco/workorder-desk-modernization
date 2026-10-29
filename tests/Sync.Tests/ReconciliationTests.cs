using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Commands;
using WorkOrders.Api.Reconcile;

namespace Sync.Tests;

/// <summary>The conflict policy and the reconciliation report, with both systems and the broker running for real.
/// The acceptance rule for the report: after any failure scenario, no difference is left unexplained.</summary>
[Collection("stack")]
public class ReconciliationTests(PostgresFixture pg, SqlServerFixture sql, RabbitFixture rabbit) : IAsyncLifetime
{
    private const int Planner = 40;

    public async Task InitializeAsync()
    {
        await rabbit.ResetAsync();
        await sql.ResetAsync();
        await using var db = await pg.CleanContextAsync();
        for (var i = 1; i <= 16; i++) db.ScrapReasons.Add(new WorkOrders.Api.Data.ScrapReason { Id = i, Name = "reason " + i });   // the import normally supplies these
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> DesktopCreate(int qty = 10)
    {
        await sql.DesktopAsync($"DECLARE @id int; EXEC dbo.uspWorkOrderCreate 514, {qty}, '2026-12-01', 27, @id OUTPUT;");
        return await sql.ScalarAsync<int>("SELECT MAX(WorkOrderID) FROM Production.WorkOrder");
    }

    private async Task<Guid> NewSideId(int legacyId)
    {
        await using var db = pg.NewContext();
        return (await db.WorkOrders.SingleAsync(w => w.LegacyWorkOrderId == legacyId)).Id;
    }

    [Fact]
    public async Task After_ordinary_work_on_both_sides_the_report_is_clean()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        var id = await DesktopCreate();
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderRelease {id}, 27");
        await system.DrainAsync();
        var order = await NewSideId(id);

        await system.AsPlanner(c => c.ChangePlanAsync(Planner, order, 14, new DateTime(2026, 12, 15)));
        await system.AsPlanner(c => c.ScrapAsync(Planner, order, 2, 7));
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 1, 8, 27");
        var created = await system.AsPlanner(c => c.CreateAsync(Planner, 514, 6, new DateTime(2026, 12, 30)));
        await system.DrainAsync();

        var report = await system.ReconcileAsync();
        Assert.Empty(report.Differences);
        Assert.True(report.Compared >= 2);
        await using var db = pg.NewContext();
        Assert.Equal(0, await db.Outbox.CountAsync(o => o.ConfirmedAt == null));   // every command was answered
        Assert.NotNull((await db.WorkOrders.SingleAsync(w => w.Id == created.Id)).LegacyWorkOrderId);
        Assert.Equal(3, (await db.WorkOrders.SingleAsync(w => w.Id == order)).ScrappedQty);
    }

    [Fact]
    public async Task A_change_still_travelling_is_reported_in_flight_and_gone_once_it_arrives()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit, pump: false);
        var id = await DesktopCreate();
        await system.DrainAsync();
        await system.StopPumpAsync();

        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 2, 7, 27");   // sits in the legacy outbox
        var held = await system.ReconcileAsync();
        var d = Assert.Single(held.Differences);
        Assert.Equal(DifferenceClass.InFlight, d.Class);
        Assert.Contains("unpublished", d.Reason);
        Assert.Equal(0, held.Unexplained);

        await system.DrainAsync();
        Assert.Empty((await system.ReconcileAsync()).Differences);
    }

    [Fact]
    public async Task A_planner_edit_not_yet_confirmed_is_in_flight()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit, pump: false);
        var id = await DesktopCreate();
        await system.DrainAsync();
        await system.StopPumpAsync();

        await system.AsPlanner(c => c.ScrapAsync(Planner, NewSideId(id).Result, 2, 7));   // optimistic locally, command still in the outbox
        var report = await system.ReconcileAsync();

        var d = Assert.Single(report.Differences);
        Assert.Equal(DifferenceClass.InFlight, d.Class);
        Assert.Contains("scrappedQty", string.Join(",", d.Fields));
        Assert.Contains("not confirmed", d.Reason);
    }

    [Fact]
    public async Task A_dead_lettered_command_is_reported_as_such_and_redriving_it_after_the_fix_clears_the_difference()
    {
        await sql.ResetAsync(patchTrigger: false);   // Microsoft's trigger as shipped: a quantity change cannot succeed
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        var id = await DesktopCreate(qty: 10);
        await system.DrainAsync();

        await system.AsPlanner(c => c.ChangePlanAsync(Planner, NewSideId(id).Result, 25, null));
        await Eventually(async () => await rabbit.CountAsync(Topology.LegacyDeadLetterQueue) == 1, "the command in the dead-letter queue");
        await system.DrainAsync();

        var stuck = await system.ReconcileAsync();
        var d = Assert.Single(stuck.Differences);
        Assert.Equal(DifferenceClass.DeadLettered, d.Class);
        Assert.Contains("ChangePlan", d.Reason);
        Assert.Equal(0, stuck.Unexplained);
        Assert.Equal("ChangePlan", Assert.Single(stuck.DeadLetters).Type);
        await using (var db = pg.NewContext())
            Assert.Equal(25, (await db.WorkOrders.SingleAsync()).OrderQty);   // the planner's optimistic value, not yet true on legacy
        Assert.Equal(10, await sql.ScalarAsync<int>($"SELECT OrderQty FROM Production.WorkOrder WHERE WorkOrderID = {id}"));

        await sql.ApplyScriptAsync("V006");                                   // ship the fix
        Assert.Equal(1, await system.Bus.RedriveAsync(Topology.LegacyDeadLetterQueue));
        await system.DrainAsync();

        var healed = await system.ReconcileAsync();
        Assert.Empty(healed.Differences);
        Assert.Empty(healed.DeadLetters);
        Assert.Equal(25, await sql.ScalarAsync<int>($"SELECT OrderQty FROM Production.WorkOrder WHERE WorkOrderID = {id}"));
    }

    [Fact]
    public async Task A_difference_nobody_can_explain_is_reported_as_unexplained()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        var id = await DesktopCreate();
        await system.DrainAsync();
        Assert.Empty((await system.ReconcileAsync()).Differences);

        // A write that bypasses the lab's triggers (someone ran a script with triggers disabled).
        await sql.ExecAsync($"""
            DISABLE TRIGGER Production.trWorkOrderSync ON Production.WorkOrder;
            UPDATE Production.WorkOrder SET DueDate = '2027-03-03' WHERE WorkOrderID = {id};
            ENABLE TRIGGER Production.trWorkOrderSync ON Production.WorkOrder;
            """);

        var report = await system.ReconcileAsync();
        var d = Assert.Single(report.Differences);
        Assert.Equal(DifferenceClass.Unexplained, d.Class);
        Assert.Contains("dueDate", string.Join(",", d.Fields));
        Assert.Equal(1, report.Unexplained);
    }

    [Fact]
    public async Task A_difference_that_clears_between_the_two_reads_is_not_reported_as_unexplained()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        var id = await DesktopCreate();
        await system.DrainAsync();

        // The same unexplained write as above, but it is put back before the second read, as a change that was only
        // crossing the databases at the moment of the first read would have arrived by then.
        await sql.ExecAsync($"""
            DISABLE TRIGGER Production.trWorkOrderSync ON Production.WorkOrder;
            UPDATE Production.WorkOrder SET DueDate = '2027-03-03' WHERE WorkOrderID = {id};
            ENABLE TRIGGER Production.trWorkOrderSync ON Production.WorkOrder;
            """);
        var report = await system.ReconcileAsync(betweenReads: () => sql.ExecAsync($"""
            DISABLE TRIGGER Production.trWorkOrderSync ON Production.WorkOrder;
            UPDATE Production.WorkOrder SET DueDate = '2026-12-01' WHERE WorkOrderID = {id};
            ENABLE TRIGGER Production.trWorkOrderSync ON Production.WorkOrder;
            """));

        Assert.Equal(0, report.Unexplained);
        Assert.Empty(report.Differences);
        Assert.Contains(report.Warnings, w => w.Contains("cleared on the second") && w.Contains(id.ToString()));
    }

    [Fact]
    public async Task A_difference_written_directly_into_the_new_database_is_also_unexplained()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit);
        await DesktopCreate();
        await system.DrainAsync();

        await using (var db = pg.NewContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE work_order SET scrapped_qty = scrapped_qty + 1");

        var report = await system.ReconcileAsync();
        Assert.Equal(1, report.Unexplained);
    }

    [Fact]
    public async Task Crossing_edits_of_the_due_date_are_logged_on_both_sides_the_planner_wins_and_the_sides_converge()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit, pump: false);
        var id = await DesktopCreate();
        await system.DrainAsync();
        await system.StopPumpAsync();
        var order = await NewSideId(id);

        await sql.ExecAsync($"UPDATE Production.WorkOrder SET DueDate = '2026-11-01' WHERE WorkOrderID = {id}");   // the floor, on the desktop
        await system.AsPlanner(c => c.ChangePlanAsync(Planner, order, null, new DateTime(2026, 12, 20)));         // the planner, on the web, not having seen it
        await system.DrainAsync();                                                                               // now everything crosses

        var report = await system.ReconcileAsync();
        Assert.Empty(report.Differences);
        Assert.Equal(new DateTime(2026, 12, 20), await sql.ScalarAsync<DateTime>($"SELECT DueDate FROM Production.WorkOrder WHERE WorkOrderID = {id}"));
        await using var db = pg.NewContext();
        Assert.Equal(new DateTime(2026, 12, 20), (await db.WorkOrders.SingleAsync()).DueDate);

        var newSide = report.Conflicts.Single(c => c.Side == "new");
        Assert.Equal(("DueDate", "new"), (newSide.Field, newSide.Winner));
        Assert.StartsWith("2026-11-01", newSide.LegacyValue);   // the legacy value we held back
        var legacySide = report.Conflicts.Single(c => c.Side == "legacy");
        Assert.Equal(("DueDate", "new"), (legacySide.Field, legacySide.Winner));
        Assert.StartsWith("2026-11-01", legacySide.LegacyValue);   // the value the command overwrote
        Assert.StartsWith("2026-12-20", legacySide.NewValue);
    }

    [Fact]
    public async Task An_edit_of_an_order_closed_meanwhile_is_refused_logged_and_the_new_side_gets_the_legacy_truth()
    {
        await using var system = await TestSystem.StartAsync(pg, sql, rabbit, pump: false);
        var id = await DesktopCreate();
        await system.DrainAsync();
        await system.StopPumpAsync();
        var order = await NewSideId(id);

        await system.AsPlanner(c => c.ScrapAsync(Planner, order, 3, 7));                                   // optimistic: scrap 3
        await sql.ExecAsync($"UPDATE Production.WorkOrder SET EndDate = GETDATE() WHERE WorkOrderID = {id}");   // the floor closed it
        await system.DrainAsync();

        var report = await system.ReconcileAsync();
        Assert.Empty(report.Differences);
        await using var db = pg.NewContext();
        var wo = await db.WorkOrders.SingleAsync();
        Assert.Equal(0, wo.ScrappedQty);                 // the planner's scrap is gone: legacy wins
        Assert.NotNull(wo.EndDate);
        Assert.Equal(0, await db.Outbox.CountAsync(o => o.ConfirmedAt == null));
        var c = Assert.Single(report.Conflicts, x => x.Side == "legacy");
        Assert.Equal(("Closed", "legacy"), (c.Field, c.Winner));
        Assert.Equal(0u, await rabbit.CountAsync(Topology.LegacyDeadLetterQueue));   // a business refusal is not retried or dead-lettered
        Assert.Equal(0, await sql.ScalarAsync<int>($"SELECT ScrappedQty FROM Production.WorkOrder WHERE WorkOrderID = {id}"));
    }

    private static async Task Eventually(Func<Task<bool>> cond, string what, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (await cond()) return; await Task.Delay(200); }
        Assert.Fail("timed out waiting for " + what);
    }
}
