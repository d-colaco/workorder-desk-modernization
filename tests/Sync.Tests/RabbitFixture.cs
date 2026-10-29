using System.Net;
using System.Net.Sockets;
using RabbitMQ.Client;
using Sync.Contracts;
using Testcontainers.RabbitMq;

namespace Sync.Tests;

/// <summary>A real RabbitMQ on a fixed host port, so stopping and starting the container (broker down) keeps the
/// same address. The queues are durable, so messages survive the restart as they would in production.</summary>
public class RabbitFixture : IAsyncLifetime
{
    private readonly int _port;
    private readonly RabbitMqContainer _rabbit;

    public RabbitFixture()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); _port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _rabbit = new RabbitMqBuilder("rabbitmq:4-management").WithPortBinding(_port, 5672).Build();
    }

    public string Uri => _rabbit.GetConnectionString();
    public Task InitializeAsync() => _rabbit.StartAsync();
    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();
    public Task StopBrokerAsync() => _rabbit.StopAsync();
    public Task StartBrokerAsync() => _rabbit.StartAsync();

    public static readonly string[] AllQueues =
    [
        Topology.NewSideQueue, Topology.NewSideDeadLetterQueue, Topology.LegacyQueue, Topology.LegacyDeadLetterQueue,
        Sync.RabbitMq.RabbitMqBus.RetryQueue(Topology.NewSideQueue), Sync.RabbitMq.RabbitMqBus.RetryQueue(Topology.LegacyQueue),
    ];

    /// <summary>Delete every queue so the next test starts empty (the bus redeclares them on connect).</summary>
    public async Task ResetAsync()
    {
        await using var conn = await new ConnectionFactory { Uri = new System.Uri(Uri) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        foreach (var q in AllQueues) await ch.QueueDeleteAsync(q);
    }

    public async Task<uint> CountAsync(string queue)
    {
        await using var conn = await new ConnectionFactory { Uri = new System.Uri(Uri) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        return (await ch.QueueDeclarePassiveAsync(queue)).MessageCount;
    }
}
