using Sync.Contracts;
using WorkOrders.Api.Data;
using WorkOrders.Api.Sync;

namespace WorkOrders.Api.Reconcile;

/// <summary>Holds the most recent report so the API can serve it without recomputing.</summary>
public class ReconciliationState
{
    private ReconciliationReport? _latest;
    public ReconciliationReport? Latest { get => Volatile.Read(ref _latest); set => Volatile.Write(ref _latest, value); }
}

/// <summary>Runs the reconciliation on a timer (Reconcile:IntervalSeconds, default 300; 0 disables) and writes
/// each report to reports/ as Markdown.</summary>
public class ReconcileTimer(IServiceScopeFactory scopes, BrokerConnection broker, ReconciliationState state,
    IConfiguration config, ILogger<ReconcileTimer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var seconds = config.GetValue("Reconcile:IntervalSeconds", 300);
        var legacy = config.GetConnectionString("LegacySql");
        if (seconds <= 0 || string.IsNullOrEmpty(legacy)) { log.LogInformation("reconciliation timer disabled"); return; }
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 15)), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(legacy, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "reconciliation failed"); }
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
    }

    public async Task<ReconciliationReport> RunOnceAsync(string legacy, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        IDeadLetterAdmin? admin = null;
        if (broker.Configured) admin = await broker.GetAsync(ct) as IDeadLetterAdmin;
        var report = await new Reconciler(scope.ServiceProvider.GetRequiredService<LabDbContext>(), legacy, admin).RunAsync(ct);
        state.Latest = report;
        ReportFiles.Write(report);
        log.LogInformation("reconciliation: {Compared} compared, {Diff} different, {Unexplained} unexplained", report.Compared, report.Differences.Count, report.Unexplained);
        return report;
    }
}

public static class ReportFiles
{
    public static string Write(ReconciliationReport r)
    {
        Directory.CreateDirectory("reports");
        var path = Path.Combine("reports", $"reconcile-{r.At:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(path, r.ToMarkdown());
        File.WriteAllText(Path.Combine("reports", "reconcile-latest.md"), r.ToMarkdown());
        return path;
    }
}
