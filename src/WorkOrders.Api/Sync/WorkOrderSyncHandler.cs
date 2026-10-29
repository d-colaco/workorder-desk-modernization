using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sync.Contracts;
using WorkOrders.Api.Data;

namespace WorkOrders.Api.Sync;

public enum HandleResult { Applied, Duplicate, Stale }

/// <summary>Applies one legacy snapshot (a change, or the confirmation of one of our own commands). It never
/// writes to the new-side outbox, so confirmations cannot echo. Idempotent: the inbox row and the change commit together, so a
/// redelivered message is recognised and skipped. Order-safe: a snapshot whose version is not newer than the
/// stored one is recorded and ignored. Snapshots are whole-order state, so a missed or reordered message is
/// repaired by the next one.</summary>
public class WorkOrderSyncHandler(LabDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HandleResult> HandleAsync(Message m, CancellationToken ct = default)
    {
        using var span = LabTelemetry.Source.StartActivity("apply " + m.Type + " on new side");
        span.TagOrder(m);
        span?.SetTag("dclab.message.version", m.Version);
        var result = await ApplyAsync(m, ct);
        span?.SetTag("dclab.apply.result", result.ToString());
        return result;
    }

    private async Task<HandleResult> ApplyAsync(Message m, CancellationToken ct)
    {
        if (await db.ProcessedMessages.AnyAsync(p => p.MessageId == m.MessageId, ct)) return HandleResult.Duplicate;
        if (m.Type == Topology.SubjectErased) return await ApplySubjectErasedAsync(m, ct);

        WorkOrder? wo = null;
        if (Guid.TryParse(m.NewId, out var newId))
        {
            // The legacy side accepted an order we created: this message carries our id, so it tells us the legacy id.
            wo = await db.WorkOrders.Include(w => w.Operations).SingleOrDefaultAsync(w => w.Id == newId, ct);
            if (wo is { LegacyWorkOrderId: null }) wo.LegacyWorkOrderId = (int)m.AggregateId;
        }
        wo ??= await db.WorkOrders.Include(w => w.Operations)
            .SingleOrDefaultAsync(w => w.LegacyWorkOrderId == m.AggregateId, ct);
        var result = HandleResult.Applied;
        var pending = m.Origin == "legacy" && wo != null ? await PendingPlanFieldsAsync((int)m.AggregateId, ct) : (Qty: false, Due: false);

        if (wo != null && wo.LegacyVersion >= m.Version)
        {
            result = HandleResult.Stale;
        }
        else
        {
            using var doc = JsonDocument.Parse(m.Body);
            if (doc.RootElement.TryGetProperty("deleted", out _))
            {
                if (wo != null) db.WorkOrders.Remove(wo);
            }
            else
            {
                var snap = doc.RootElement.Deserialize<Snapshot>(Json)!;
                if (wo == null)
                {
                    wo = new WorkOrder { LegacyWorkOrderId = (int)m.AggregateId };
                    db.WorkOrders.Add(wo);
                }
                else
                {
                    db.Operations.RemoveRange(wo.Operations);
                    wo.Operations = new();
                }
                var (keepQty, keepDue) = (wo.OrderQty, wo.DueDate);
                wo.LegacyVersion = m.Version;
                wo.LegacyProductId = snap.ProductId; wo.ProductName = snap.ProductName; wo.OrderQty = snap.OrderQty;
                wo.ScrappedQty = snap.ScrappedQty; wo.StartDate = snap.StartDate; wo.EndDate = snap.EndDate;
                wo.DueDate = snap.DueDate; wo.ScrapReasonId = snap.ScrapReasonId;
                // Conflict policy: quantity and due date belong to the planner desk. While our own edit of one of
                // them is unconfirmed, a legacy change to it does not overwrite ours (our command will overwrite
                // theirs on the legacy side); the conflict is logged so the overwrite is visible.
                if (pending.Qty && snap.OrderQty != keepQty) { LogConflict(wo, "OrderQty", snap.OrderQty.ToString(), keepQty.ToString(), m); wo.OrderQty = keepQty; }
                if (pending.Due && snap.DueDate != keepDue) { LogConflict(wo, "DueDate", snap.DueDate.ToString("s"), keepDue.ToString("s"), m); wo.DueDate = keepDue; }
                foreach (var o in snap.Operations)
                    wo.Operations.Add(new WorkOrderOperation
                    {
                        OperationSequence = o.OperationSequence, LocationId = o.LocationId, LocationName = o.LocationName,
                        ScheduledStartDate = o.ScheduledStartDate, ScheduledEndDate = o.ScheduledEndDate,
                        ActualStartDate = o.ActualStartDate, ActualEndDate = o.ActualEndDate,
                        ActualResourceHrs = o.ActualResourceHrs, PlannedCost = o.PlannedCost, ActualCost = o.ActualCost,
                    });
            }
        }

        await MarkConfirmedAsync(m, ct);
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = m.MessageId, ProcessedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return HandleResult.Duplicate;   // a concurrent delivery of the same message won the race
        }
        return result;
    }

    /// <summary>The legacy side has erased a data subject. Purge every copy this side holds: the planner's label (and
    /// their ability to sign in), and the actor reference inside outbox payloads. Idempotent.</summary>
    private async Task<HandleResult> ApplySubjectErasedAsync(Message m, CancellationToken ct)
    {
        var subject = (int)m.AggregateId;
        using var doc = JsonDocument.Parse(m.Body);
        var requestId = doc.RootElement.GetProperty("requestId").GetGuid();
        var detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;

        var planner = await db.Planners.SingleOrDefaultAsync(p => p.BusinessEntityId == subject, ct);
        if (planner != null) { planner.DisplayName = null; planner.ErasedAt ??= DateTime.UtcNow; }
        var rewritten = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE outbox SET payload = jsonb_set(payload, '{{actorBusinessEntityId}}', '-1'::jsonb) WHERE (payload->>'actorBusinessEntityId') = {subject.ToString()}", ct);

        var log = await db.ErasureLog.SingleOrDefaultAsync(l => l.RequestId == requestId, ct);
        if (log == null) db.ErasureLog.Add(log = new ErasureLogEntry { RequestId = requestId, SubjectBusinessEntityId = subject, RequestedAt = DateTime.UtcNow });
        log.LegacyConfirmedAt ??= DateTime.UtcNow;
        log.NewPurgedAt = DateTime.UtcNow;
        log.Detail = $"legacy: {detail}; new: planner label cleared, {rewritten} outbox payload(s) rewritten";

        await MarkConfirmedAsync(m, ct);
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = m.MessageId, ProcessedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { return HandleResult.Duplicate; }
        return HandleResult.Applied;
    }

    /// <summary>Which planning fields have an edit of ours that the legacy side has not confirmed yet.</summary>
    private async Task<(bool Qty, bool Due)> PendingPlanFieldsAsync(int legacyId, CancellationToken ct)
    {
        var payloads = await db.Outbox.Where(o => o.Type == Topology.ChangePlan && o.LegacyWorkOrderId == legacyId && o.ConfirmedAt == null)
            .Select(o => o.Payload).ToListAsync(ct);
        bool qty = false, due = false;
        foreach (var p in payloads)
        {
            using var d = JsonDocument.Parse(p);
            qty |= d.RootElement.TryGetProperty("orderQty", out var q) && q.ValueKind != JsonValueKind.Null;
            due |= d.RootElement.TryGetProperty("dueDate", out var t) && t.ValueKind != JsonValueKind.Null;
        }
        return (qty, due);
    }

    private void LogConflict(WorkOrder wo, string field, string legacy, string ours, Message m) =>
        db.Conflicts.Add(new ConflictLogEntry
        {
            LegacyWorkOrderId = wo.LegacyWorkOrderId!.Value, Field = field, LegacyValue = legacy, NewValue = ours,
            Winner = "new", MessageId = m.MessageId, CreatedAt = DateTime.UtcNow,
        });

    /// <summary>A message that answers one of our commands (CommandId = "new-&lt;outbox id&gt;") closes it.</summary>
    private async Task MarkConfirmedAsync(Message m, CancellationToken ct)
    {
        if (m.CommandId is not { } c || !c.StartsWith("new-") || !long.TryParse(c.AsSpan(4), out var id)) return;
        var row = await db.Outbox.SingleOrDefaultAsync(o => o.Id == id, ct);
        if (row is { ConfirmedAt: null }) row.ConfirmedAt = DateTime.UtcNow;
    }

    private sealed record Snapshot(int ProductId, string ProductName, int OrderQty, int ScrappedQty, DateTime StartDate,
        DateTime? EndDate, DateTime DueDate, int? ScrapReasonId, List<Op> Operations);

    private sealed record Op(short OperationSequence, short LocationId, string LocationName, DateTime ScheduledStartDate,
        DateTime ScheduledEndDate, DateTime? ActualStartDate, DateTime? ActualEndDate, decimal? ActualResourceHrs,
        decimal PlannedCost, decimal? ActualCost);
}
