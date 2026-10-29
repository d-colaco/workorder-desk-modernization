using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Sync.Contracts;
using Sync.Legacy;
using Sync.RabbitMq;
using WorkOrders.Api.Data;
using WorkOrders.Api.Sync;

namespace Sync.Tests;

/// <summary>Failure cases that need a real broker: poison messages, a consumer that dies before acknowledging,
/// the broker going away, and a trigger rollback that ends in the dead-letter queue.</summary>
[Collection("stack")]
public class BrokerFailureTests(PostgresFixture pg, SqlServerFixture sql, RabbitFixture rabbit) : IAsyncLifetime
{
    private static readonly RabbitMqOptions Fast = new(TimeSpan.FromMilliseconds(300), MaxAttempts: 3);

    public async Task InitializeAsync()
    {
        await rabbit.ResetAsync();
        await sql.ResetAsync();
        await using var _ = await pg.CleanContextAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task Eventually(Func<Task<bool>> cond, string what, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            try { if (await cond()) return; } catch (Exception) { /* broker or database still coming back */ }
            await Task.Delay(200);
        }
        Assert.Fail("timed out waiting for " + what);
    }

    private static string Snapshot(int order, int scrapped = 0) => $$"""
        {"workOrderId":{{order}},"productId":514,"productName":"Seat","orderQty":10,"scrappedQty":{{scrapped}},
         "startDate":"2026-10-12T09:00:00","endDate":null,"dueDate":"2026-12-01T00:00:00","scrapReasonId":null,"operations":[]}
        """;

    private static Message Changed(string id, int order, long version, int scrapped = 0) =>
        new(id, Topology.WorkOrderChanged, order, version, "legacy", Encoding.UTF8.GetBytes(Snapshot(order, scrapped)));

    private async Task ApplyToNewSide(Message m, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        await new WorkOrderSyncHandler(db).HandleAsync(m, ct);
    }

    [Fact]
    public async Task A_poison_message_is_retried_then_lands_in_the_dead_letter_queue_with_its_error()
    {
        await using var bus = await RabbitMqBus.ConnectAsync(rabbit.Uri, options: Fast);
        var attempts = 0;
        await using var sub = await bus.SubscribeAsync(Topology.NewSideQueue, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("malformed payload");
        });

        await bus.PublishAsync(Topology.LegacyEvents, new Message("bad-1", Topology.WorkOrderChanged, 1, 1, "legacy", Encoding.UTF8.GetBytes("not json")));

        await Eventually(async () => await rabbit.CountAsync(Topology.NewSideDeadLetterQueue) == 1, "the message in the dead-letter queue");
        Assert.Equal(3, attempts);   // MaxAttempts deliveries, no more
        Assert.Equal(0u, await rabbit.CountAsync(Topology.NewSideQueue));
        Assert.Equal(0u, await rabbit.CountAsync(RabbitMqBus.RetryQueue(Topology.NewSideQueue)));

        await using var conn = await new ConnectionFactory { Uri = new Uri(rabbit.Uri) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        var dead = await ch.BasicGetAsync(Topology.NewSideDeadLetterQueue, autoAck: true);
        Assert.Equal("bad-1", dead!.BasicProperties.MessageId);
        Assert.Equal(3, Convert.ToInt32(dead.BasicProperties.Headers!["x-attempt"]));
        Assert.Contains("malformed payload", Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers["x-error"]!));
    }

    [Fact]
    public async Task A_message_that_fails_once_succeeds_on_retry_and_is_applied_once()
    {
        await using var bus = await RabbitMqBus.ConnectAsync(rabbit.Uri, options: Fast);
        var failures = 0;
        await using var sub = await bus.SubscribeAsync(Topology.NewSideQueue, async (m, ct) =>
        {
            if (Interlocked.Increment(ref failures) == 1) throw new TimeoutException("database busy");
            await ApplyToNewSide(m, ct);
        });

        await bus.PublishAsync(Topology.LegacyEvents, Changed("legacy-1", 500, 1));

        await Eventually(async () => await pg.NewContext().WorkOrders.CountAsync() == 1, "the order on the new side");
        Assert.Equal(0u, await rabbit.CountAsync(Topology.NewSideDeadLetterQueue));
    }

    [Fact]
    public async Task A_consumer_that_dies_after_committing_but_before_acking_gets_the_message_again_and_it_changes_nothing()
    {
        await using var bus = await RabbitMqBus.ConnectAsync(rabbit.Uri, options: Fast);   // declares the topology
        var committed = new TaskCompletionSource();

        // A consumer that commits the change and then dies (connection drops) without acknowledging.
        var dying = await new ConnectionFactory { Uri = new Uri(rabbit.Uri) }.CreateConnectionAsync();
        var dyingCh = await dying.CreateChannelAsync();
        var raw = new AsyncEventingBasicConsumer(dyingCh);
        raw.ReceivedAsync += async (_, ea) =>
        {
            await ApplyToNewSide(new Message(ea.BasicProperties.MessageId!, Topology.WorkOrderChanged, 500, 1, "legacy", ea.Body.ToArray()), default);
            committed.TrySetResult();   // ...and never acks
        };
        await dyingCh.BasicConsumeAsync(Topology.NewSideQueue, autoAck: false, raw);

        await bus.PublishAsync(Topology.LegacyEvents, Changed("legacy-7", 500, 1));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await dying.AbortAsync();   // the process "crashes"

        var results = new List<HandleResult>();
        await using var sub = await bus.SubscribeAsync(Topology.NewSideQueue, async (m, ct) =>
        {
            await using var db = pg.NewContext();
            lock (results) results.Add(default);
            var r = await new WorkOrderSyncHandler(db).HandleAsync(m, ct);
            lock (results) results[^1] = r;
        });

        await Eventually(async () => { lock (results) return results.Count == 1 && results[0] == HandleResult.Duplicate; }, "the redelivery to be recognised as a duplicate");
        await using var check = pg.NewContext();
        Assert.Equal(1, await check.WorkOrders.CountAsync());
        Assert.Equal(1, await check.ProcessedMessages.CountAsync());
        await Eventually(async () => await rabbit.CountAsync(Topology.NewSideQueue) == 0, "the queue to drain");
    }

    [Fact]
    public async Task Broker_down_loses_nothing_the_outbox_keeps_the_changes_and_the_relay_resumes_in_order()
    {
        await using var relayBus = await RabbitMqBus.ConnectAsync(rabbit.Uri, options: Fast);
        await using var consumerBus = await RabbitMqBus.ConnectAsync(rabbit.Uri, options: Fast);
        await using var sub = await consumerBus.SubscribeAsync(Topology.NewSideQueue, (m, ct) => ApplyToNewSide(m, ct));
        var relay = new Worker(relayBus, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:LegacySql"] = sql.AppConnectionString }).Build(), NullLogger<Worker>.Instance);

        await sql.DesktopAsync("DECLARE @id int; EXEC dbo.uspWorkOrderCreate 514, 10, '2026-12-01', 27, @id OUTPUT;");
        var id = await sql.ScalarAsync<int>("SELECT MAX(WorkOrderID) FROM Production.WorkOrder");
        Assert.Equal(1, await relay.RelayBatchAsync(default));
        await Eventually(async () => await pg.NewContext().WorkOrders.CountAsync() == 1, "the first change to arrive while the broker is up");

        await rabbit.StopBrokerAsync();
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 1, 7, 27");   // the floor keeps working on the desktop app
        await sql.DesktopAsync($"EXEC dbo.uspWorkOrderScrap {id}, 2, 7, 27");

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            await Assert.ThrowsAnyAsync<Exception>(() => relay.RelayBatchAsync(cts.Token));
        Assert.Equal(2, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Outbox WHERE PublishedAt IS NULL"));   // nothing lost, nothing marked

        await rabbit.StartBrokerAsync();
        await Eventually(async () => { await relay.RelayBatchAsync(default); return await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Outbox WHERE PublishedAt IS NULL") == 0; },
            "the relay to catch up after the broker returns", seconds: 90);

        await Eventually(async () => (await pg.NewContext().WorkOrders.SingleAsync()).ScrappedQty == 3, "the new side to catch up", seconds: 60);
        await using var check = pg.NewContext();
        Assert.Equal(3, await check.ProcessedMessages.CountAsync());   // create + 2 scraps, each exactly once
    }

    [Fact]
    public async Task A_trigger_rollback_on_the_legacy_side_retries_then_dead_letters_the_command_and_changes_nothing()
    {
        await sql.ResetAsync(patchTrigger: false);   // Microsoft's trigger as shipped
        await sql.DesktopAsync("DECLARE @id int; EXEC dbo.uspWorkOrderCreate 514, 10, '2026-12-01', 27, @id OUTPUT;");
        var id = await sql.ScalarAsync<int>("SELECT MAX(WorkOrderID) FROM Production.WorkOrder");

        await using var bus = await RabbitMqBus.ConnectAsync(rabbit.Uri, options: Fast);
        var handler = new LegacyCommandHandler(sql.AppConnectionString);
        var attempts = 0;
        await using var sub = await bus.SubscribeAsync(Topology.LegacyQueue, async (m, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await handler.HandleAsync(m, ct);
        });

        await bus.PublishAsync(Topology.NewEvents, new Message("new-9", Topology.ChangePlan, id, 0, "new",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ChangePlanCommand(20, null, 40), new JsonSerializerOptions(JsonSerializerDefaults.Web)))));

        await Eventually(async () => await rabbit.CountAsync(Topology.LegacyDeadLetterQueue) == 1, "the command in legacy.commands.dlq");
        Assert.Equal(3, attempts);
        Assert.Equal(10, await sql.ScalarAsync<int>($"SELECT OrderQty FROM Production.WorkOrder WHERE WorkOrderID = {id}"));
        Assert.Equal(0, await sql.ScalarAsync<int>("SELECT COUNT(*) FROM sync.Inbox"));
    }

    [Fact]
    public async Task New_side_relay_leaves_commands_unpublished_when_the_broker_fails_then_publishes_each_once_in_order()
    {
        await using var db = pg.NewContext();
        foreach (var n in new[] { 1, 2, 3 })
            db.Outbox.Add(new OutboxMessage { Type = Topology.RecordScrap, LegacyWorkOrderId = 100, CreatedAt = DateTime.UtcNow, Payload = $"{{\"quantity\":{n},\"reasonId\":7,\"actorBusinessEntityId\":27}}" });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<IOException>(() => OutboxRelay.RelayBatchAsync(db, new DownBus(), default));
        await using var mid = pg.NewContext();
        Assert.Equal(3, await mid.Outbox.CountAsync(o => o.PublishedAt == null));

        var sent = new RecordingBus();
        await using var db2 = pg.NewContext();
        Assert.Equal(3, await OutboxRelay.RelayBatchAsync(db2, sent, default));
        var order = sent.Ids.Select(i => long.Parse(i["new-".Length..])).ToList();
        Assert.Equal(order.Order().ToList(), order);   // published in outbox order
        Assert.Equal(3, order.Distinct().Count());
        await using var after = pg.NewContext();
        Assert.Equal(0, await after.Outbox.CountAsync(o => o.PublishedAt == null));
    }

    private sealed class DownBus : IMessageBus
    {
        public Task PublishAsync(string destination, Message message, CancellationToken ct = default) => throw new IOException("broker unreachable");
        public Task<IAsyncDisposable> SubscribeAsync(string queue, Func<Message, CancellationToken, Task> handler, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingBus : IMessageBus
    {
        public List<string> Ids { get; } = new();
        public Task PublishAsync(string destination, Message message, CancellationToken ct = default) { Ids.Add(message.MessageId); return Task.CompletedTask; }
        public Task<IAsyncDisposable> SubscribeAsync(string queue, Func<Message, CancellationToken, Task> handler, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
