using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sync.Contracts;
using Sync.Legacy;
using Sync.RabbitMq;
using WorkOrders.Api.Commands;
using WorkOrders.Api.Reconcile;
using WorkOrders.Api.Sync;

namespace Sync.Tests;

/// <summary>Both directions of the sync running in-process against the real containers: the legacy relay and the
/// new-side relay (which the pump drives), plus the consumers on both sides. The pump can be stopped to hold
/// changes in the outboxes, which is how a test makes a change "in flight" or makes two edits cross.</summary>
public sealed class TestSystem : IAsyncDisposable
{
    private readonly PostgresFixture _pg;
    private readonly SqlServerFixture _sql;
    private readonly RabbitFixture _rabbit;
    private readonly RabbitMqBus _bus;
    private readonly Worker _legacyRelay;
    private IAsyncDisposable? _newSideConsumer, _legacyConsumer;
    private CancellationTokenSource? _pump;
    private Task? _pumpTask;

    private TestSystem(PostgresFixture pg, SqlServerFixture sql, RabbitFixture rabbit, RabbitMqBus bus)
    {
        (_pg, _sql, _rabbit, _bus) = (pg, sql, rabbit, bus);
        _legacyRelay = new Worker(bus, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:LegacySql"] = sql.AppConnectionString }).Build(), NullLogger<Worker>.Instance);
    }

    public RabbitMqBus Bus => _bus;

    public static async Task<TestSystem> StartAsync(PostgresFixture pg, SqlServerFixture sql, RabbitFixture rabbit, RabbitMqOptions? options = null, bool pump = true)
    {
        var bus = await RabbitMqBus.ConnectAsync(rabbit.Uri, options: options ?? new(TimeSpan.FromMilliseconds(300), 3));
        var s = new TestSystem(pg, sql, rabbit, bus);
        var commands = new LegacyCommandHandler(sql.AppConnectionString);
        s._newSideConsumer = await bus.SubscribeAsync(Topology.NewSideQueue, async (m, ct) =>
        {
            await using var db = pg.NewContext();
            await new WorkOrderSyncHandler(db).HandleAsync(m, ct);
        });
        s._legacyConsumer = await bus.SubscribeAsync(Topology.LegacyQueue, (m, ct) => commands.HandleAsync(m, ct));
        if (pump) s.StartPump();
        return s;
    }

    public void StartPump()
    {
        if (_pump != null) return;
        _pump = new CancellationTokenSource();
        var ct = _pump.Token;
        _pumpTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _legacyRelay.RelayBatchAsync(ct);
                    await using var db = _pg.NewContext();
                    await OutboxRelay.RelayBatchAsync(db, _bus, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* retried on the next tick */ }
                await Task.Delay(100, CancellationToken.None);
            }
        });
    }

    public async Task StopPumpAsync()
    {
        if (_pump == null) return;
        _pump.Cancel();
        try { await _pumpTask!; } catch (OperationCanceledException) { }
        _pump = null;
    }

    /// <summary>Wait until nothing is waiting anywhere: outboxes published, queues and retry queues empty, stable over a few polls.</summary>
    public async Task DrainAsync(int seconds = 40)
    {
        StartPump();
        var calm = 0;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var idle = await _sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Outbox WHERE PublishedAt IS NULL") == 0;
            await using (var db = _pg.NewContext())
                idle &= !await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Outbox, o => o.PublishedAt == null);
            foreach (var q in new[] { Topology.NewSideQueue, Topology.LegacyQueue, RabbitMqBus.RetryQueue(Topology.NewSideQueue), RabbitMqBus.RetryQueue(Topology.LegacyQueue) })
                idle &= await _rabbit.CountAsync(q) == 0;
            calm = idle ? calm + 1 : 0;
            if (calm >= 4) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("the system did not go quiet");
    }

    /// <param name="betweenReads">Runs after a first read that found something unexplained, before the second read.</param>
    public async Task<ReconciliationReport> ReconcileAsync(Func<Task>? betweenReads = null)
    {
        await using var db = _pg.NewContext();
        return await new Reconciler(db, _sql.AppConnectionString, _bus, TimeSpan.FromMilliseconds(100),
            betweenReads == null ? null : _ => betweenReads()).RunAsync();
    }

    /// <summary>The planner desk's API, as a given planner.</summary>
    public async Task<CommandResponse> AsPlanner(Func<WorkOrderCommands, Task<CommandResponse>> action)
    {
        await using var db = _pg.NewContext();
        var r = await action(new WorkOrderCommands(db));
        Xunit.Assert.True(r.Outcome == CommandOutcome.Accepted, $"planner command was not accepted: {r.Outcome} {r.Error}");   // a refused edit would make a scenario test prove nothing
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        await StopPumpAsync();
        if (_newSideConsumer != null) await _newSideConsumer.DisposeAsync();
        if (_legacyConsumer != null) await _legacyConsumer.DisposeAsync();
        await _bus.DisposeAsync();
    }
}
