namespace WorkOrders.Api.Data;

public class WorkOrder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>AdventureWorks Production.WorkOrder.WorkOrderID; null for an order created on the new side and not yet assigned.</summary>
    public int? LegacyWorkOrderId { get; set; }
    /// <summary>Version of the last legacy change applied (sync.AggregateVersion); older messages are ignored.</summary>
    public long LegacyVersion { get; set; }
    public int LegacyProductId { get; set; }
    public string ProductName { get; set; } = "";
    public int OrderQty { get; set; }
    public int ScrappedQty { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime DueDate { get; set; }
    public int? ScrapReasonId { get; set; }
    public List<WorkOrderOperation> Operations { get; set; } = new();

    // Same derivation as dbo.vWorkOrderStatus on the legacy side.
    public string Status =>
        EndDate != null ? "Closed"
        : Operations.Any(o => o.ActualStartDate != null) ? "In progress"
        : Operations.Count > 0 ? "Released"
        : "Created";
}

/// <summary>Legacy composite key (WorkOrderID, ProductID, OperationSequence) is rebuilt from the parent's
/// legacy id, <see cref="WorkOrder.LegacyProductId"/> and <see cref="OperationSequence"/>.</summary>
public class WorkOrderOperation
{
    public Guid WorkOrderId { get; set; }
    public short OperationSequence { get; set; }
    public short LocationId { get; set; }
    public string LocationName { get; set; } = "";
    public DateTime ScheduledStartDate { get; set; }
    public DateTime ScheduledEndDate { get; set; }
    public DateTime? ActualStartDate { get; set; }
    public DateTime? ActualEndDate { get; set; }
    public decimal? ActualResourceHrs { get; set; }
    public decimal PlannedCost { get; set; }
    public decimal? ActualCost { get; set; }
}

/// <summary>Inbox: one row per message already applied, committed in the same transaction as its effect.</summary>
public class ProcessedMessage
{
    public string MessageId { get; set; } = "";
    public DateTime ProcessedAt { get; set; }
}

/// <summary>New-side outbox: a command for the legacy side, written in the same transaction as the local change.</summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    /// <summary>Legacy WorkOrderID the command targets; null for a create.</summary>
    public int? LegacyWorkOrderId { get; set; }
    /// <summary>For an erasure request: the data subject (an AdventureWorks BusinessEntityID). Null otherwise.</summary>
    public int? SubjectBusinessEntityId { get; set; }
    /// <summary>For a create: the new side's id, which the legacy side echoes back so the new side learns the legacy id.</summary>
    public Guid? NewId { get; set; }
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    /// <summary>When the legacy side's confirmation of this command arrived (or its refusal). Null = still in flight.</summary>
    public DateTime? ConfirmedAt { get; set; }
    /// <summary>W3C traceparent of the request that wrote this command, so the trace continues through the broker.</summary>
    public string? TraceParent { get; set; }
}

/// <summary>A conflict resolved on the new side: a legacy change arrived while our own edit of the same field
/// was still unconfirmed. The legacy-side counterpart is sync.ConflictLog.</summary>
public class ConflictLogEntry
{
    public long Id { get; set; }
    public int LegacyWorkOrderId { get; set; }
    public string Field { get; set; } = "";
    public string? LegacyValue { get; set; }
    public string? NewValue { get; set; }
    public string Winner { get; set; } = "";
    public string? MessageId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ScrapReason
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A planner who may use the new side. The display name is the only personal data the new side holds about a
/// person (the UI needs a label); erasure removes it and the planner can no longer sign in.</summary>
public class Planner
{
    public int BusinessEntityId { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? ErasedAt { get; set; }
}

/// <summary>Evidence that an erasure ran on this side. No personal data: ids, times and counts.</summary>
public class ErasureLogEntry
{
    public Guid RequestId { get; set; }
    public int SubjectBusinessEntityId { get; set; }
    public DateTime RequestedAt { get; set; }
    /// <summary>When the legacy side (the owner of the person data) reported it was done.</summary>
    public DateTime? LegacyConfirmedAt { get; set; }
    /// <summary>When this side finished purging its own copies.</summary>
    public DateTime? NewPurgedAt { get; set; }
    public string? Detail { get; set; }
}
