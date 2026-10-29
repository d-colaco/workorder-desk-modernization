namespace Sync.Contracts;

/// <summary>What travels between the systems. <see cref="Body"/> is the JSON snapshot; everything the
/// receiver needs to deduplicate and order it is in the headers and <see cref="MessageId"/>.</summary>
public sealed record Message(
    string MessageId,
    string Type,
    long AggregateId,
    long Version,
    string Origin,
    ReadOnlyMemory<byte> Body,
    string? TraceParent = null,
    string? NewId = null,
    string? CommandId = null,
    DateTime? EnqueuedAt = null);

/// <summary>The only thing the sync code knows about the broker. RabbitMQ implements it here;
/// Azure Service Bus could replace it without touching the relay or the handlers.</summary>
public interface IMessageBus : IAsyncDisposable
{
    /// <summary>Returns only after the broker has confirmed it took responsibility for the message.</summary>
    Task PublishAsync(string destination, Message message, CancellationToken ct = default);

    /// <summary>The handler's success acknowledges the message; an exception rejects it to the dead-letter queue.</summary>
    Task<IAsyncDisposable> SubscribeAsync(string queue, Func<Message, CancellationToken, Task> handler, CancellationToken ct = default);
}

/// <summary>One message sitting in a dead-letter queue, as seen by the reconciliation report.</summary>
public sealed record DeadLetter(string Queue, string MessageId, string Type, long AggregateId, string? NewId, string? CommandId, string? Error, int Attempts);

public sealed record QueuedText(string Queue, string Text);

/// <summary>Operating the dead-letter queues: look without consuming, and put messages back once the cause is fixed.</summary>
public interface IDeadLetterAdmin
{
    Task<IReadOnlyList<DeadLetter>> PeekAsync(string deadLetterQueue, CancellationToken ct = default);
    /// <summary>Moves every message back to its main queue with the attempt counter reset. Returns how many.</summary>
    Task<int> RedriveAsync(string deadLetterQueue, CancellationToken ct = default);
    /// <summary>Ready messages per queue (what a person would read off the management page).</summary>
    Task<IReadOnlyDictionary<string, uint>> DepthsAsync(IEnumerable<string> queues, CancellationToken ct = default);
    /// <summary>Headers and body of every message in the given queues, as text, without consuming them. For the erasure check.</summary>
    Task<IReadOnlyList<QueuedText>> PeekTextAsync(IEnumerable<string> queues, CancellationToken ct = default);
    /// <summary>Rewrites every queued message whose JSON body names the subject as its actor, replacing the actor with
    /// the tombstone -1. The message is taken, rewritten and put back on the same queue. Returns how many.</summary>
    Task<int> RedactActorAsync(IEnumerable<string> queues, int subjectBusinessEntityId, CancellationToken ct = default);
}

public static class Topology
{
    // legacy -> new
    public const string LegacyEvents = "legacy.events";
    public const string NewSideQueue = "newside.workorders";
    public const string NewSideDeadLetterQueue = "newside.workorders.dlq";
    // new -> legacy
    public const string NewEvents = "new.events";
    public const string LegacyQueue = "legacy.commands";
    public const string LegacyDeadLetterQueue = "legacy.commands.dlq";
    public const string DeadLetterExchange = "dlx";

    /// <summary>A change made on the legacy side by anyone but the sync consumer.</summary>
    public const string WorkOrderChanged = "WorkOrderChanged";
    /// <summary>The legacy result of a command from the new side. Carries the legacy state (and, for a create,
    /// the new side's id) so the new side learns the outcome. It is state, never a command: applying it
    /// writes nothing to the new side's outbox, so it cannot loop.</summary>
    public const string WorkOrderConfirmed = "WorkOrderConfirmed";

    public const string CreateWorkOrder = "CreateWorkOrder";
    public const string ChangePlan = "ChangePlan";
    public const string RecordScrap = "RecordScrap";

    /// <summary>new -> legacy: erase a data subject on the side that owns the person data.</summary>
    public const string EraseSubject = "EraseSubject";
    /// <summary>legacy -> new: the subject is erased on the legacy side; purge your copies.</summary>
    public const string SubjectErased = "SubjectErased";
}

/// <summary>Command bodies (new -> legacy). The actor is an AdventureWorks BusinessEntityID; no names travel.</summary>
public record CreateWorkOrderCommand(int ProductId, int OrderQty, DateTime DueDate, int ActorBusinessEntityId);
/// <param name="BaseOrderQty">The quantity the planner saw when editing. If the legacy side holds something else, that is a conflict.</param>
/// <param name="BaseDueDate">The due date the planner saw when editing.</param>
public record ChangePlanCommand(int? OrderQty, DateTime? DueDate, int ActorBusinessEntityId,
    int? BaseOrderQty = null, DateTime? BaseDueDate = null);
/// <summary>Scrap travels as a delta, so scrap entered on both sides adds up instead of conflicting.</summary>
public record RecordScrapCommand(int Quantity, int ReasonId, int ActorBusinessEntityId);
