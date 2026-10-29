using Sync.Contracts;

namespace WorkOrders.Api.Sync;

/// <summary>Subscribes the new side to the legacy events queue. Starts only when RabbitMq:Uri is configured.</summary>
public class SyncConsumer(IServiceScopeFactory scopes, BrokerConnection broker, ILogger<SyncConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!broker.Configured) { log.LogInformation("RabbitMq:Uri not set; sync consumer disabled"); return; }
        var bus = await broker.GetAsync(ct);
        await using var sub = await bus.SubscribeAsync(Topology.NewSideQueue, async (m, token) =>
        {
            using var scope = scopes.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<WorkOrderSyncHandler>().HandleAsync(m, token);
            log.LogInformation("{Id} {Type} order {Order} v{Version}: {Result}", m.MessageId, m.Type, LabTelemetry.LoggableId(m), m.Version, result);
        }, ct);
        await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
    }
}
