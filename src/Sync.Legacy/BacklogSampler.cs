using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;
using Sync.Contracts;

namespace Sync.Legacy;

/// <summary>Reports how much is waiting in the legacy outbox and how old the oldest row is. Lag is recorded only
/// when a message gets through, so during a broker outage the histogram says nothing; these two gauges are what
/// show the outage building up and draining.</summary>
public class BacklogSampler : BackgroundService
{
    private long _backlog;
    private double _oldestAgeSeconds;
    private readonly string _sql;
    private readonly ILogger<BacklogSampler> _log;

    public BacklogSampler(IConfiguration config, ILogger<BacklogSampler> log)
    {
        _sql = config.GetConnectionString("LegacySql") ?? throw new InvalidOperationException("ConnectionStrings:LegacySql is not set");
        _log = log;
        LabTelemetry.Meter.CreateObservableGauge("dclab.outbox.backlog", () => new Measurement<long>(Volatile.Read(ref _backlog), new KeyValuePair<string, object?>("side", "legacy")), "{row}", "Unpublished outbox rows");
        LabTelemetry.Meter.CreateObservableGauge("dclab.outbox.oldest_age", () => new Measurement<double>(Volatile.Read(ref _oldestAgeSeconds), new KeyValuePair<string, object?>("side", "legacy")), "s", "Age of the oldest unpublished outbox row");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var c = new SqlConnection(_sql);
                await c.OpenAsync(ct);
                await using var cmd = new SqlCommand(
                    "SELECT COUNT(*), ISNULL(DATEDIFF_BIG(millisecond, MIN(CreatedAt), SYSUTCDATETIME()) / 1000.0, 0) FROM sync.Outbox WHERE PublishedAt IS NULL", c);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    Volatile.Write(ref _backlog, r.GetInt32(0));
                    Volatile.Write(ref _oldestAgeSeconds, Convert.ToDouble(r.GetValue(1)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "backlog sample failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
