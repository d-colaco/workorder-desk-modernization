using Sync.Contracts;

namespace Sync.Legacy;

/// <summary>Consumes commands from the new side. A failing command is rejected to legacy.commands.dlq.</summary>
public class CommandConsumer(IMessageBus bus, IConfiguration config, ILogger<CommandConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var handler = new LegacyCommandHandler(config.GetConnectionString("LegacySql")
            ?? throw new InvalidOperationException("ConnectionStrings:LegacySql is not set"));
        await using var sub = await bus.SubscribeAsync(Topology.LegacyQueue, async (m, token) =>
        {
            try
            {
                var result = await handler.HandleAsync(m, token);
                log.LogInformation("{Id} {Type} order {Order}: {Result}", m.MessageId, m.Type, LabTelemetry.LoggableId(m), result);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "{Id} {Type} order {Order} rejected", m.MessageId, m.Type, LabTelemetry.LoggableId(m));
                throw;
            }
        }, ct);
        await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
    }
}
