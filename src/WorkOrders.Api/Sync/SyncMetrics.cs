using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Data;
using WorkOrders.Api.Reconcile;

namespace WorkOrders.Api.Sync;

/// <summary>Samples what is waiting on the new side and in the broker: PostgreSQL outbox backlog and oldest age,
/// unconfirmed commands, queue depths, and the latest reconciliation result. Lag is only recorded when a message
/// gets through, so these gauges are what show an outage building up and draining.</summary>
public class SyncMetrics : BackgroundService
{
    private static readonly string[] Queues =
    [
        Topology.NewSideQueue, Topology.LegacyQueue, Topology.NewSideDeadLetterQueue, Topology.LegacyDeadLetterQueue,
        global::Sync.RabbitMq.RabbitMqBus.RetryQueue(Topology.NewSideQueue), global::Sync.RabbitMq.RabbitMqBus.RetryQueue(Topology.LegacyQueue),
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly BrokerConnection _broker;
    private readonly ReconciliationState _reconciliation;
    private long _backlog, _unconfirmed;
    private double _oldestAge;
    private volatile IReadOnlyDictionary<string, uint> _depths = new Dictionary<string, uint>();

    public SyncMetrics(IServiceScopeFactory scopes, BrokerConnection broker, ReconciliationState reconciliation)
    {
        (_scopes, _broker, _reconciliation) = (scopes, broker, reconciliation);
        var m = LabTelemetry.Meter;
        m.CreateObservableGauge("dclab.outbox.backlog", () => new Measurement<long>(Volatile.Read(ref _backlog), new KeyValuePair<string, object?>("side", "new")), "{row}", "Unpublished outbox rows");
        m.CreateObservableGauge("dclab.outbox.oldest_age", () => new Measurement<double>(Volatile.Read(ref _oldestAge), new KeyValuePair<string, object?>("side", "new")), "s", "Age of the oldest unpublished outbox row");
        m.CreateObservableGauge("dclab.commands.unconfirmed", () => Volatile.Read(ref _unconfirmed), "{command}", "Planner commands the legacy side has not answered");
        // Empty while the broker cannot be read, so the dashboard shows a gap rather than a stale number.
        m.CreateObservableGauge("dclab.queue.depth", () => _depths.Select(kv => new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("queue", kv.Key))), "{message}", "Ready messages per queue");
        m.CreateObservableGauge("dclab.reconcile.unexplained", () => (long)(_reconciliation.Latest?.Unexplained ?? 0), "{order}", "Unexplained differences in the latest reconciliation");
        m.CreateObservableGauge("dclab.reconcile.differences", () => (long)(_reconciliation.Latest?.Differences.Count ?? 0), "{order}", "Differences (any class) in the latest reconciliation");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LabDbContext>();
                var pending = await db.Outbox.AsNoTracking().Where(o => o.PublishedAt == null).Select(o => o.CreatedAt).ToListAsync(ct);
                Volatile.Write(ref _backlog, pending.Count);
                Volatile.Write(ref _oldestAge, pending.Count == 0 ? 0 : (DateTime.UtcNow - pending.Min()).TotalSeconds);
                Volatile.Write(ref _unconfirmed, await db.Outbox.CountAsync(o => o.ConfirmedAt == null, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* database not reachable: keep the last values */ }

            _depths = new Dictionary<string, uint>();
            if (_broker.Configured && _broker.Connected)
            {
                try { if (await _broker.GetAsync(ct) is global::Sync.Contracts.IDeadLetterAdmin admin) _depths = await admin.DepthsAsync(Queues, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* broker unreachable */ }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
