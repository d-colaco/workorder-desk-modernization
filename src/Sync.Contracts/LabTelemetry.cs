using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sync.Contracts;

/// <summary>The lab's one ActivitySource and Meter. They live in the contracts assembly (System.Diagnostics only, no
/// OpenTelemetry package) so the bus, the relays and the handlers can emit spans and metrics; each host decides
/// whether to export them.</summary>
public static class LabTelemetry
{
    public const string Name = "DcLab.Sync";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    /// <summary>Time from the outbox row being written to the consumer committing its effect, in seconds. Both
    /// ends are server clocks on one host; never the desktop client's clock.</summary>
    public static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("dclab.sync.lag", "s",
        "Outbox row written to consumer commit");

    /// <summary>Outcomes per consumed message: ok, retry or dead_letter.</summary>
    public static readonly Counter<long> Messages = Meter.CreateCounter<long>("dclab.sync.messages", "{message}", "Messages consumed, by outcome");

    /// <summary>A relay could not publish or could not read its outbox.</summary>
    public static readonly Counter<long> RelayFailures = Meter.CreateCounter<long>("dclab.sync.relay.failures", "{failure}", "Relay failures");

    public static readonly double[] LagBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300];

    /// <summary>Starts a span as a child of a W3C traceparent that was carried in a message or a database row.</summary>
    public static Activity? StartChildOf(string name, ActivityKind kind, string? traceparent, DateTimeOffset? startTime = null)
    {
        if (traceparent != null && ActivityContext.TryParse(traceparent, null, out var ctx))
            return Source.StartActivity(name, kind, ctx, startTime: startTime ?? default);
        return Source.StartActivity(name, kind);
    }

    /// <summary>Records a wait that happened somewhere with no code of ours running (a row in an outbox, a message in a
    /// queue) as a span with its real start time, and returns its context for the next hop to hang from.</summary>
    public static string? RecordWait(string name, string? parentTraceparent, DateTime startUtc, ActivityKind kind = ActivityKind.Internal)
    {
        using var a = StartChildOf(name, kind, parentTraceparent, new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc)));
        return a?.Id ?? parentTraceparent;
    }

    /// <summary>Tags a span with the order a message is about. Erasure messages are about a person, so they get no id:
    /// telemetry must not carry the data subject's identifier.</summary>
    public static void TagOrder(this Activity? a, Message m)
    {
        if (m.Type is Topology.EraseSubject or Topology.SubjectErased) return;
        a?.SetTag("dclab.order.legacy_id", m.AggregateId);
    }

    /// <summary>The id to put in a log line: the order for order messages, nothing for erasure messages (their aggregate
    /// id is the data subject).</summary>
    public static long LoggableId(Message m) => m.Type is Topology.EraseSubject or Topology.SubjectErased ? 0 : m.AggregateId;

    public static string FormatTime(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O");
    public static DateTime? ParseTime(string? s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;
}
