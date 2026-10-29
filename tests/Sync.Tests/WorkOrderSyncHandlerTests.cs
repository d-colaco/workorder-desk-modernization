using System.Text;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Sync;

namespace Sync.Tests;

[Collection("stack")]
public class WorkOrderSyncHandlerTests(PostgresFixture pg)
{
    private static Message Msg(string id, long order, long version, int scrapped = 0, bool withOp = true) => new(
        id, Topology.WorkOrderChanged, order, version, "legacy", Encoding.UTF8.GetBytes($$"""
        {"workOrderId":{{order}},"productId":722,"productName":"LL Road Frame","orderQty":10,"scrappedQty":{{scrapped}},
         "startDate":"2026-10-12T08:00:00","endDate":null,"dueDate":"2026-10-26T00:00:00","scrapReasonId":null,
         "operations":[{{(withOp ? """{"operationSequence":1,"locationId":10,"locationName":"Frame Forming","scheduledStartDate":"2026-10-12T08:00:00","scheduledEndDate":"2026-10-12T16:00:00","actualStartDate":null,"actualEndDate":null,"actualResourceHrs":null,"plannedCost":10.5,"actualCost":null}""" : "")}}]}
        """));

    [Fact]
    public async Task First_message_creates_the_order_with_its_operations()
    {
        await using var db = await pg.CleanContextAsync();
        var result = await new WorkOrderSyncHandler(db).HandleAsync(Msg("m1", 100, 1));

        Assert.Equal(HandleResult.Applied, result);
        var wo = await pg.NewContext().WorkOrders.Include(w => w.Operations).SingleAsync();
        Assert.Equal(100, wo.LegacyWorkOrderId);
        Assert.Equal("Released", wo.Status);
        Assert.Single(wo.Operations);
    }

    [Fact]
    public async Task Duplicate_delivery_changes_nothing()
    {
        await using var db = await pg.CleanContextAsync();
        await new WorkOrderSyncHandler(db).HandleAsync(Msg("m1", 100, 1, scrapped: 0));

        // Redelivery of the same message id, even with a different body, is skipped: the inbox decides.
        await using var again = pg.NewContext();
        var result = await new WorkOrderSyncHandler(again).HandleAsync(Msg("m1", 100, 1, scrapped: 7));

        Assert.Equal(HandleResult.Duplicate, result);
        await using var check = pg.NewContext();
        Assert.Equal(1, await check.WorkOrders.CountAsync());
        Assert.Equal(0, (await check.WorkOrders.SingleAsync()).ScrappedQty);
        Assert.Equal(1, await check.ProcessedMessages.CountAsync());
    }

    [Fact]
    public async Task Concurrent_delivery_of_the_same_message_applies_it_once()
    {
        await using var seed = await pg.CleanContextAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = pg.NewContext();
            return await new WorkOrderSyncHandler(db).HandleAsync(Msg("race", 100, 1));
        }));

        Assert.Equal(1, results.Count(r => r == HandleResult.Applied));
        Assert.Equal(3, results.Count(r => r == HandleResult.Duplicate));
        await using var check = pg.NewContext();
        Assert.Equal(1, await check.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task Out_of_order_older_version_is_ignored_and_the_newer_state_stays()
    {
        await using var db = await pg.CleanContextAsync();
        await new WorkOrderSyncHandler(db).HandleAsync(Msg("m2", 100, 2, scrapped: 3));

        await using var late = pg.NewContext();
        var result = await new WorkOrderSyncHandler(late).HandleAsync(Msg("m1", 100, 1, scrapped: 0, withOp: false));

        Assert.Equal(HandleResult.Stale, result);
        await using var check = pg.NewContext();
        var wo = await check.WorkOrders.Include(w => w.Operations).SingleAsync();
        Assert.Equal(3, wo.ScrappedQty);
        Assert.Equal(2, wo.LegacyVersion);
        Assert.Single(wo.Operations);
        Assert.Equal(2, await check.ProcessedMessages.CountAsync());   // the stale one is recorded, so it is not retried
    }

    [Fact]
    public async Task Newer_snapshot_replaces_operations()
    {
        await using var db = await pg.CleanContextAsync();
        await new WorkOrderSyncHandler(db).HandleAsync(Msg("m1", 100, 1));
        await using var next = pg.NewContext();
        await new WorkOrderSyncHandler(next).HandleAsync(Msg("m2", 100, 2, scrapped: 2));

        await using var check = pg.NewContext();
        var wo = await check.WorkOrders.Include(w => w.Operations).SingleAsync();
        Assert.Equal(2, wo.ScrappedQty);
        Assert.Single(wo.Operations);
    }

    [Fact]
    public async Task Delete_message_removes_the_order()
    {
        await using var db = await pg.CleanContextAsync();
        await new WorkOrderSyncHandler(db).HandleAsync(Msg("m1", 100, 1));
        await using var next = pg.NewContext();
        var del = new Message("m2", Topology.WorkOrderChanged, 100, 2, "legacy", Encoding.UTF8.GetBytes("{\"deleted\":true}"));

        Assert.Equal(HandleResult.Applied, await new WorkOrderSyncHandler(next).HandleAsync(del));
        await using var check = pg.NewContext();
        Assert.Equal(0, await check.WorkOrders.CountAsync());
        Assert.Equal(0, await check.Operations.CountAsync());
    }
}
