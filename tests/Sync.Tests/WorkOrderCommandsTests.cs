using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Commands;
using WorkOrders.Api.Data;
using WorkOrders.Api.Sync;

namespace Sync.Tests;

[Collection("stack")]
public class WorkOrderCommandsTests(PostgresFixture pg)
{
    private static readonly Guid Known = Guid.CreateVersion7();

    private async Task<LabDbContext> SeedAsync(int? legacyId = 100, bool closed = false, int scrapped = 0)
    {
        var db = await pg.CleanContextAsync();
        db.ScrapReasons.Add(new ScrapReason { Id = 7, Name = "Handling damage" });
        db.WorkOrders.Add(new WorkOrder
        {
            Id = Known, LegacyWorkOrderId = legacyId, LegacyProductId = 722, ProductName = "LL Road Frame", OrderQty = 10,
            ScrappedQty = scrapped, StartDate = new DateTime(2026, 10, 12), DueDate = new DateTime(2026, 10, 26),
            EndDate = closed ? new DateTime(2026, 10, 20) : null,
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Create_writes_the_order_and_its_command_in_one_commit_and_the_order_is_pending()
    {
        await using var db = await SeedAsync();
        var r = await new WorkOrderCommands(db).CreateAsync(27, 722, 5, new DateTime(2026, 12, 1));

        Assert.Equal(CommandOutcome.Accepted, r.Outcome);
        await using var check = pg.NewContext();
        var created = await check.WorkOrders.SingleAsync(w => w.Id == r.Id);
        Assert.Null(created.LegacyWorkOrderId);
        var msg = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.CreateWorkOrder, msg.Type);
        Assert.Equal(r.Id, msg.NewId);
        Assert.Equal(27, JsonDocument.Parse(msg.Payload).RootElement.GetProperty("actorBusinessEntityId").GetInt32());
    }

    [Fact]
    public async Task Create_for_an_unknown_product_is_rejected_and_writes_nothing()
    {
        await using var db = await SeedAsync();
        var r = await new WorkOrderCommands(db).CreateAsync(27, 99999, 5, new DateTime(2026, 12, 1));

        Assert.Equal(CommandOutcome.Invalid, r.Outcome);
        await using var check = pg.NewContext();
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Edits_of_a_pending_order_wait_for_the_legacy_id()
    {
        await using var db = await SeedAsync(legacyId: null);
        var c = new WorkOrderCommands(db);

        Assert.Equal(CommandOutcome.Pending, (await c.ChangePlanAsync(27, Known, 20, null)).Outcome);
        Assert.Equal(CommandOutcome.Pending, (await c.ScrapAsync(27, Known, 1, 7)).Outcome);
        await using var check = pg.NewContext();
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Closed_orders_cannot_be_edited()
    {
        await using var db = await SeedAsync(closed: true);
        Assert.Equal(CommandOutcome.Closed, (await new WorkOrderCommands(db).ChangePlanAsync(27, Known, 20, null)).Outcome);
    }

    [Theory]
    [InlineData(0, 7)]     // not positive
    [InlineData(9, 7)]     // 3 already scrapped + 9 > 10
    [InlineData(1, 99)]    // unknown reason
    public async Task Invalid_scrap_is_rejected_without_a_command(int qty, int reason)
    {
        await using var db = await SeedAsync(scrapped: 3);
        var r = await new WorkOrderCommands(db).ScrapAsync(27, Known, qty, reason);

        Assert.Equal(CommandOutcome.Invalid, r.Outcome);
        await using var check = pg.NewContext();
        Assert.Equal(0, await check.Outbox.CountAsync());
        Assert.Equal(3, (await check.WorkOrders.SingleAsync()).ScrappedQty);
    }

    [Fact]
    public async Task Scrap_is_sent_as_a_delta_and_applied_locally()
    {
        await using var db = await SeedAsync(scrapped: 3);
        await new WorkOrderCommands(db).ScrapAsync(40, Known, 2, 7);

        await using var check = pg.NewContext();
        Assert.Equal(5, (await check.WorkOrders.SingleAsync()).ScrappedQty);
        var body = JsonDocument.Parse((await check.Outbox.SingleAsync()).Payload).RootElement;
        Assert.Equal(2, body.GetProperty("quantity").GetInt32());   // the delta, not the total
    }

    [Fact]
    public async Task Quantity_below_what_is_scrapped_is_rejected()
    {
        await using var db = await SeedAsync(scrapped: 3);
        Assert.Equal(CommandOutcome.Invalid, (await new WorkOrderCommands(db).ChangePlanAsync(27, Known, 2, null)).Outcome);
    }

    [Fact]
    public async Task Confirmation_of_a_create_assigns_the_legacy_id_and_does_not_echo()
    {
        await using var db = await SeedAsync();
        var created = await new WorkOrderCommands(db).CreateAsync(27, 722, 5, new DateTime(2026, 12, 1));
        await using var check0 = pg.NewContext();
        Assert.Equal(1, await check0.Outbox.CountAsync());

        // What the legacy side sends back after running the create procedure: origin "new", our id in the header.
        var snapshot = """
            {"workOrderId":200,"productId":722,"productName":"LL Road Frame","orderQty":5,"scrappedQty":0,
             "startDate":"2026-10-12T09:00:00","endDate":null,"dueDate":"2026-12-01T00:00:00","scrapReasonId":null,"operations":[]}
            """;
        var confirmed = new Message("legacy-50", Topology.WorkOrderConfirmed, 200, 1, "new", Encoding.UTF8.GetBytes(snapshot),
            NewId: created.Id.ToString());
        await using var db2 = pg.NewContext();
        Assert.Equal(HandleResult.Applied, await new WorkOrderSyncHandler(db2).HandleAsync(confirmed));

        await using var check = pg.NewContext();
        Assert.Equal(200, (await check.WorkOrders.SingleAsync(w => w.Id == created.Id)).LegacyWorkOrderId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.PublishedAt == null));   // still only the original command
        Assert.Equal(2, await check.WorkOrders.CountAsync());                             // mapped onto our row, no duplicate

        // Redelivery of the confirmation changes nothing either.
        await using var db3 = pg.NewContext();
        Assert.Equal(HandleResult.Duplicate, await new WorkOrderSyncHandler(db3).HandleAsync(confirmed));
    }

    [Fact]
    public async Task Legacy_truth_replaces_the_optimistic_local_state()
    {
        await using var db = await SeedAsync();
        await new WorkOrderCommands(db).ChangePlanAsync(27, Known, 25, null);   // local optimistic: 25

        var snapshot = """
            {"workOrderId":100,"productId":722,"productName":"LL Road Frame","orderQty":12,"scrappedQty":0,
             "startDate":"2026-10-12T00:00:00","endDate":null,"dueDate":"2026-10-26T00:00:00","scrapReasonId":null,"operations":[]}
            """;
        await using var db2 = pg.NewContext();
        await new WorkOrderSyncHandler(db2).HandleAsync(
            new Message("legacy-9", Topology.WorkOrderConfirmed, 100, 3, "new", Encoding.UTF8.GetBytes(snapshot)));

        await using var check = pg.NewContext();
        Assert.Equal(12, (await check.WorkOrders.SingleAsync()).OrderQty);
    }
}
