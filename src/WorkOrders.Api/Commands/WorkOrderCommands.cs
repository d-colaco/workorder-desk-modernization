using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Data;

namespace WorkOrders.Api.Commands;

public enum CommandOutcome { Accepted, NotFound, Pending, Closed, Invalid }

public record CommandResponse(CommandOutcome Outcome, Guid? Id = null, string? Error = null);

/// <summary>The planner desk's writes. Each applies the change locally (so the planner sees it at once) and
/// writes a command to the outbox in the same transaction. The legacy side applies it through its stored
/// procedures; its confirmation then replaces our optimistic state with the legacy truth.</summary>
public class WorkOrderCommands(LabDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CommandResponse> CreateAsync(int actor, int productId, int orderQty, DateTime dueDate, CancellationToken ct = default)
    {
        if (orderQty <= 0) return new(CommandOutcome.Invalid, Error: "Quantity must be positive.");
        var name = await db.WorkOrders.Where(w => w.LegacyProductId == productId).Select(w => w.ProductName).FirstOrDefaultAsync(ct);
        if (name == null) return new(CommandOutcome.Invalid, Error: "Unknown product.");

        var wo = new WorkOrder
        {
            LegacyProductId = productId, ProductName = name, OrderQty = orderQty, DueDate = dueDate,
            StartDate = DateTime.Now,   // plant wall-clock, like the legacy side
        };
        db.WorkOrders.Add(wo);
        Enqueue(Topology.CreateWorkOrder, null, wo.Id, new CreateWorkOrderCommand(productId, orderQty, dueDate, actor));
        await db.SaveChangesAsync(ct);
        return new(CommandOutcome.Accepted, wo.Id);
    }

    public async Task<CommandResponse> ChangePlanAsync(int actor, Guid id, int? orderQty, DateTime? dueDate, CancellationToken ct = default)
    {
        var (wo, fail) = await LoadEditableAsync(id, ct);
        if (wo == null) return fail!;
        if (orderQty is <= 0) return new(CommandOutcome.Invalid, Error: "Quantity must be positive.");
        if (orderQty < wo.ScrappedQty) return new(CommandOutcome.Invalid, Error: "Quantity is below what is already scrapped.");
        if (orderQty == null && dueDate == null) return new(CommandOutcome.Invalid, Error: "Nothing to change.");

        // The command carries what the planner saw, so the legacy side can tell if someone changed it meanwhile.
        var command = new ChangePlanCommand(orderQty, dueDate, actor, wo.OrderQty, wo.DueDate);
        if (orderQty != null) wo.OrderQty = orderQty.Value;
        if (dueDate != null) wo.DueDate = dueDate.Value;
        Enqueue(Topology.ChangePlan, wo.LegacyWorkOrderId, null, command);
        await db.SaveChangesAsync(ct);
        return new(CommandOutcome.Accepted, wo.Id);
    }

    public async Task<CommandResponse> ScrapAsync(int actor, Guid id, int quantity, int reasonId, CancellationToken ct = default)
    {
        var (wo, fail) = await LoadEditableAsync(id, ct);
        if (wo == null) return fail!;
        if (quantity <= 0) return new(CommandOutcome.Invalid, Error: "Scrap quantity must be positive.");
        if (wo.ScrappedQty + quantity > wo.OrderQty) return new(CommandOutcome.Invalid, Error: "Scrap would exceed the order quantity.");
        if (!await db.ScrapReasons.AnyAsync(r => r.Id == reasonId, ct)) return new(CommandOutcome.Invalid, Error: "Unknown scrap reason.");

        wo.ScrappedQty += quantity;
        wo.ScrapReasonId = reasonId;
        Enqueue(Topology.RecordScrap, wo.LegacyWorkOrderId, null, new RecordScrapCommand(quantity, reasonId, actor));
        await db.SaveChangesAsync(ct);
        return new(CommandOutcome.Accepted, wo.Id);
    }

    private async Task<(WorkOrder?, CommandResponse?)> LoadEditableAsync(Guid id, CancellationToken ct)
    {
        var wo = await db.WorkOrders.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (wo == null) return (null, new(CommandOutcome.NotFound));
        // Until the legacy id arrives, edits would have nothing to target: the planner waits.
        if (wo.LegacyWorkOrderId == null) return (null, new(CommandOutcome.Pending, Error: "Waiting for the legacy system to accept this order."));
        if (wo.EndDate != null) return (null, new(CommandOutcome.Closed, Error: "Work order is closed."));
        return (wo, null);
    }

    private void Enqueue(string type, int? legacyId, Guid? newId, object body) =>
        db.Outbox.Add(new OutboxMessage
        {
            Type = type, LegacyWorkOrderId = legacyId, NewId = newId, CreatedAt = DateTime.UtcNow, TraceParent = System.Diagnostics.Activity.Current?.Id,
            Payload = JsonSerializer.Serialize(body, Json),
        });
}
