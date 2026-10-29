using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sync.Contracts;
using WorkOrders.Api.Data;

namespace WorkOrders.Api.Sync;

/// <summary>Publishes new-side commands in outbox order, then marks them published (at-least-once; the legacy
/// inbox absorbs a republish after a crash between the two steps).</summary>
public class OutboxRelay(IServiceScopeFactory scopes, BrokerConnection broker, IConfiguration config, ILogger<OutboxRelay> log) : BackgroundService
{
    private readonly TimeSpan _idle = TimeSpan.FromMilliseconds(config.GetValue("Sync:PollMilliseconds", 200));
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!broker.Configured) return;
        var bus = await broker.GetAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            int sent = 0;
            try
            {
                using var scope = scopes.CreateScope();
                sent = await RelayBatchAsync(scope.ServiceProvider.GetRequiredService<LabDbContext>(), bus, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LabTelemetry.RelayFailures.Add(1, new KeyValuePair<string, object?>("side", "new"));
                log.LogWarning(ex, "outbox relay failed; retrying");
                await Task.Delay(2000, ct);
            }
            if (sent == 0) await Task.Delay(_idle, ct);
        }
    }

    public static async Task<int> RelayBatchAsync(LabDbContext db, IMessageBus bus, CancellationToken ct)
    {
        var batch = await db.Outbox.Where(o => o.PublishedAt == null).OrderBy(o => o.Id).Take(100).ToListAsync(ct);
        foreach (var o in batch)
        {
            // The command waited in the outbox with none of our code running: record that as a span with its real start.
            var afterWait = LabTelemetry.RecordWait("new outbox wait", o.TraceParent, o.CreatedAt);
            using var relay = LabTelemetry.StartChildOf("new outbox relay", ActivityKind.Internal, afterWait);
            relay?.SetTag("dclab.command.type", o.Type);
            await bus.PublishAsync(Topology.NewEvents, new Message($"new-{o.Id}", o.Type, o.LegacyWorkOrderId ?? o.SubjectBusinessEntityId ?? 0, 0, "new",
                Encoding.UTF8.GetBytes(o.Payload), relay?.Id ?? o.TraceParent, o.NewId?.ToString(), $"new-{o.Id}", o.CreatedAt), ct);
            o.PublishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return batch.Count;
    }
}
