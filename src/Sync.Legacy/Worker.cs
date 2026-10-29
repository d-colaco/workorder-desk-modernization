using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;
using Sync.Contracts;

namespace Sync.Legacy;

/// <summary>Relay: publishes unpublished sync.Outbox rows in OutboxId order, then marks them published.
/// At-least-once: a crash between the confirm and the UPDATE republishes the row, and the receiver's
/// inbox absorbs it.</summary>
public class Worker(IMessageBus bus, IConfiguration config, ILogger<Worker> log) : BackgroundService
{
    private readonly string _sql = config.GetConnectionString("LegacySql")
        ?? throw new InvalidOperationException("ConnectionStrings:LegacySql is not set");
    // How long the relay sleeps when the outbox is empty. This is the floor under the sync lag (Sync:PollMilliseconds).
    private readonly TimeSpan Idle = TimeSpan.FromMilliseconds(config.GetValue("Sync:PollMilliseconds", 200));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int sent = 0;
            try { sent = await RelayBatchAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LabTelemetry.RelayFailures.Add(1, new KeyValuePair<string, object?>("side", "legacy"));
                log.LogWarning(ex, "relay failed; retrying");   // broker or database down: rows stay unpublished
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            if (sent == 0) await Task.Delay(Idle, ct);
        }
    }

    public async Task<int> RelayBatchAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_sql);
        await conn.OpenAsync(ct);
        var batch = new List<(long OutboxId, Message Msg)>();
        await using (var cmd = new SqlCommand(
            @"SELECT TOP (100) OutboxId, AggregateId, Version, Origin, TraceParent, Payload, NewId, CommandId, CreatedAt, AggregateType
              FROM sync.Outbox WHERE PublishedAt IS NULL ORDER BY OutboxId", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var origin = r.GetString(3);
                var type = r.GetString(9) == "Erasure" ? Topology.SubjectErased
                    : origin == "new" ? Topology.WorkOrderConfirmed : Topology.WorkOrderChanged;
                batch.Add((r.GetInt64(0), new Message($"legacy-{r.GetInt64(0)}", type,
                    r.GetInt32(1), r.GetInt64(2), origin, Encoding.UTF8.GetBytes(r.GetString(5)),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(6) ? null : r.GetGuid(6).ToString(), r.IsDBNull(7) ? null : r.GetString(7), r.GetDateTime(8))));
            }
        }
        foreach (var (id, msg) in batch)
        {
            // The row waited in the outbox with none of our code running: record that wait as a span with its real
            // start, so the trace shows it, then relay under a span that hangs from it.
            var afterWait = LabTelemetry.RecordWait("legacy outbox wait", msg.TraceParent, msg.EnqueuedAt!.Value);
            using var relay = LabTelemetry.StartChildOf("legacy outbox relay", ActivityKind.Internal, afterWait);
            relay.TagOrder(msg);
            relay?.SetTag("dclab.message.origin", msg.Origin);
            await bus.PublishAsync(Topology.LegacyEvents, msg with { TraceParent = relay?.Id ?? msg.TraceParent }, ct);
            await using var mark = new SqlCommand("UPDATE sync.Outbox SET PublishedAt = SYSUTCDATETIME() WHERE OutboxId = @id", conn);
            mark.Parameters.AddWithValue("@id", id);
            await mark.ExecuteNonQueryAsync(ct);
        }
        if (batch.Count > 0) log.LogInformation("relayed {Count} outbox rows up to {Last}", batch.Count, batch[^1].OutboxId);
        return batch.Count;
    }
}
