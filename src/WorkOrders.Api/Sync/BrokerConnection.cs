using Sync.Contracts;
using Sync.RabbitMq;

namespace WorkOrders.Api.Sync;

/// <summary>One shared bus for the consumer and the outbox relay. Connects lazily and keeps retrying, so the
/// API starts and serves reads while the broker is down.</summary>
public sealed class BrokerConnection(IConfiguration config, ILogger<BrokerConnection> log) : IAsyncDisposable
{
    private IMessageBus? _bus;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True once connected; lets samplers avoid forcing a connection attempt of their own.</summary>
    public bool Connected => _bus != null;

    public bool Configured => !string.IsNullOrEmpty(config["RabbitMq:Uri"]);

    public async Task<IMessageBus> GetAsync(CancellationToken ct)
    {
        if (_bus != null) return _bus;
        await _gate.WaitAsync(ct);
        try
        {
            while (_bus == null)
            {
                try { _bus = await RabbitMqBus.ConnectAsync(config["RabbitMq:Uri"]!, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("broker not reachable ({Message}); retrying", ex.Message);
                    await Task.Delay(2000, ct);
                }
            }
            return _bus;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() { if (_bus != null) await _bus.DisposeAsync(); }
}
