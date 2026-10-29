using System.Diagnostics;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Sync.Contracts;

namespace Sync.RabbitMq;

/// <param name="RetryDelay">How long a failed message waits in the retry queue before it is delivered again.</param>
/// <param name="MaxAttempts">Deliveries in all (the first plus retries). After the last failure the message goes to the dead-letter queue.</param>
public sealed record RabbitMqOptions(TimeSpan RetryDelay, int MaxAttempts)
{
    public static RabbitMqOptions Default { get; } = new(TimeSpan.FromSeconds(5), 4);
}

public sealed class RabbitMqBus : IMessageBus, IDeadLetterAdmin
{
    private const string AttemptHeader = "x-attempt";
    private const string ErrorHeader = "x-error";
    private readonly RabbitMqOptions _options;
    private readonly IConnection _conn;
    private readonly IChannel _publish;

    private RabbitMqBus(IConnection conn, IChannel publish, RabbitMqOptions options) { _conn = conn; _publish = publish; _options = options; }

    public static async Task<RabbitMqBus> ConnectAsync(string uri, CancellationToken ct = default, RabbitMqOptions? options = null)
    {
        options ??= RabbitMqOptions.Default;
        var factory = new ConnectionFactory { Uri = new Uri(uri), AutomaticRecoveryEnabled = true };
        var conn = await factory.CreateConnectionAsync(ct);
        // Publisher confirms: PublishAsync completes only once the broker has the message.
        var publish = await conn.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await DeclareTopologyAsync(publish, options, ct);
        return new RabbitMqBus(conn, publish, options);
    }

    /// <summary>Idempotent. A rejected message goes to the dead-letter exchange and lands in the .dlq queue.</summary>
    public static async Task DeclareTopologyAsync(IChannel ch, RabbitMqOptions options, CancellationToken ct = default)
    {
        await ch.ExchangeDeclareAsync(Topology.DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        foreach (var (exchange, queue, dlq) in new[]
        {
            (Topology.LegacyEvents, Topology.NewSideQueue, Topology.NewSideDeadLetterQueue),
            (Topology.NewEvents, Topology.LegacyQueue, Topology.LegacyDeadLetterQueue),
        })
        {
            await ch.QueueDeclareAsync(dlq, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
            await ch.QueueBindAsync(dlq, Topology.DeadLetterExchange, queue, cancellationToken: ct);

            await ch.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
            await ch.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                    ["x-dead-letter-routing-key"] = queue,
                }, cancellationToken: ct);
            await ch.QueueBindAsync(queue, exchange, queue, cancellationToken: ct);

            // Retry queue: a failed message waits here for RetryDelay, then returns to the main queue via the
            // default exchange. No consumer reads it.
            await ch.QueueDeclareAsync(RetryQueue(queue), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-message-ttl"] = (long)options.RetryDelay.TotalMilliseconds,
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = queue,
                }, cancellationToken: ct);
        }
    }

    public async Task<IReadOnlyList<DeadLetter>> PeekAsync(string deadLetterQueue, CancellationToken ct = default)
    {
        await using var ch = await _conn.CreateChannelAsync(cancellationToken: ct);
        var found = new List<DeadLetter>();
        // Fetch without acking, then close the channel: unacknowledged messages go back to the queue untouched.
        while (found.Count < 1000 && await ch.BasicGetAsync(deadLetterQueue, autoAck: false, ct) is { } r)
        {
            var m = ToMessage(r.BasicProperties, r.Body);
            var h = r.BasicProperties.Headers;
            found.Add(new DeadLetter(deadLetterQueue, m.MessageId, m.Type, m.AggregateId, m.NewId, m.CommandId,
                h != null && h.TryGetValue(ErrorHeader, out var e) ? e is byte[] b ? Encoding.UTF8.GetString(b) : e?.ToString() : null,
                h != null && h.TryGetValue(AttemptHeader, out var a) ? Convert.ToInt32(a) : 0));
        }
        await ch.CloseAsync(cancellationToken: ct);
        return found;
    }

    public async Task<int> RedriveAsync(string deadLetterQueue, CancellationToken ct = default)
    {
        var main = deadLetterQueue.EndsWith(".dlq") ? deadLetterQueue[..^4] : throw new ArgumentException("not a dead-letter queue");
        await using var ch = await _conn.CreateChannelAsync(cancellationToken: ct);
        var moved = 0;
        while (await ch.BasicGetAsync(deadLetterQueue, autoAck: false, ct) is { } r)
        {
            var headers = new Dictionary<string, object?>(r.BasicProperties.Headers ?? new Dictionary<string, object?>());
            headers.Remove(AttemptHeader); headers.Remove(ErrorHeader);
            var props = new BasicProperties
            {
                MessageId = r.BasicProperties.MessageId, Type = r.BasicProperties.Type, ContentType = r.BasicProperties.ContentType,
                DeliveryMode = DeliveryModes.Persistent, Headers = headers,
            };
            await _publish.BasicPublishAsync("", main, mandatory: true, props, r.Body, ct);   // confirmed before we ack
            await ch.BasicAckAsync(r.DeliveryTag, false, ct);
            moved++;
        }
        return moved;
    }

    public async Task<IReadOnlyDictionary<string, uint>> DepthsAsync(IEnumerable<string> queues, CancellationToken ct = default)
    {
        var result = new Dictionary<string, uint>();
        foreach (var q in queues)
        {
            await using var ch = await _conn.CreateChannelAsync(cancellationToken: ct);   // a passive declare of a missing queue closes its channel
            result[q] = (await ch.QueueDeclarePassiveAsync(q, ct)).MessageCount;
        }
        return result;
    }

    public async Task<IReadOnlyList<QueuedText>> PeekTextAsync(IEnumerable<string> queues, CancellationToken ct = default)
    {
        var all = new List<QueuedText>();
        foreach (var q in queues)
        {
            await using var ch = await _conn.CreateChannelAsync(cancellationToken: ct);
            while (await ch.BasicGetAsync(q, autoAck: false, ct) is { } r)
            {
                var headers = string.Join(";", (r.BasicProperties.Headers ?? new Dictionary<string, object?>())
                    .Select(h => h.Key + "=" + (h.Value is byte[] b ? Encoding.UTF8.GetString(b) : h.Value)));
                all.Add(new QueuedText(q, $"{r.BasicProperties.MessageId}|{r.BasicProperties.Type}|{headers}|{Encoding.UTF8.GetString(r.Body.Span)}"));
            }
            await ch.CloseAsync(cancellationToken: ct);   // nothing was acked: every message returns to its queue untouched
        }
        return all;
    }

    public async Task<int> RedactActorAsync(IEnumerable<string> queues, int subjectBusinessEntityId, CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var q in queues)
        {
            await using var ch = await _conn.CreateChannelAsync(cancellationToken: ct);
            var keep = new List<(BasicGetResult R, byte[]? NewBody)>();
            while (await ch.BasicGetAsync(q, autoAck: false, ct) is { } r)
                keep.Add((r, Redact(r.Body.ToArray(), subjectBusinessEntityId)));
            foreach (var (r, body) in keep)
            {
                if (body == null) continue;   // untouched messages simply go back when the channel closes
                var headers = new Dictionary<string, object?>(r.BasicProperties.Headers ?? new Dictionary<string, object?>());
                var props = new BasicProperties
                {
                    MessageId = r.BasicProperties.MessageId, Type = r.BasicProperties.Type, ContentType = r.BasicProperties.ContentType,
                    DeliveryMode = DeliveryModes.Persistent, Headers = headers,
                };
                await _publish.BasicPublishAsync("", q, mandatory: true, props, body, ct);   // confirmed before the original is acked
                await ch.BasicAckAsync(r.DeliveryTag, false, ct);
                changed++;
            }
            await ch.CloseAsync(cancellationToken: ct);
        }
        return changed;
    }

    private static byte[]? Redact(byte[] body, int subject)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
            if (node != null && node.TryGetPropertyValue("actorBusinessEntityId", out var v) && v?.GetValueKind() == System.Text.Json.JsonValueKind.Number && (int)v == subject)
            {
                node["actorBusinessEntityId"] = -1;
                return Encoding.UTF8.GetBytes(node.ToJsonString());
            }
        }
        catch (System.Text.Json.JsonException) { /* not JSON: leave it */ }
        return null;
    }

    public static string RetryQueue(string queue) => queue + ".retry";

    public async Task PublishAsync(string destination, Message m, CancellationToken ct = default)
    {
        using var span = LabTelemetry.Source.StartActivity("publish " + destination, ActivityKind.Producer);
        span?.SetTag("messaging.system", "rabbitmq");
        span?.SetTag("messaging.destination.name", destination);
        span?.SetTag("messaging.message.id", m.MessageId);
        span?.SetTag("dclab.message.type", m.Type);
        span.TagOrder(m);
        var props = new BasicProperties
        {
            MessageId = m.MessageId, Type = m.Type, ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>
            {
                ["aggregate-id"] = m.AggregateId, ["version"] = m.Version, ["origin"] = m.Origin,
                ["traceparent"] = span?.Id ?? m.TraceParent, ["new-id"] = m.NewId, ["command-id"] = m.CommandId,
                ["enqueued-at"] = m.EnqueuedAt is { } e ? LabTelemetry.FormatTime(e) : null, ["published-at"] = LabTelemetry.FormatTime(DateTime.UtcNow),
            },
        };
        // Direct exchanges; the routing key is the consuming side's queue name.
        var routingKey = destination switch
        {
            Topology.LegacyEvents => Topology.NewSideQueue,
            Topology.NewEvents => Topology.LegacyQueue,
            _ => throw new ArgumentException("unknown destination " + destination),
        };
        await _publish.BasicPublishAsync(destination, routingKey, mandatory: true, props, m.Body, ct);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string queue, Func<Message, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        var ch = await _conn.CreateChannelAsync(cancellationToken: ct);
        await ch.BasicQosAsync(0, 10, false, ct);
        var consumer = new AsyncEventingBasicConsumer(ch);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var message = ToMessage(ea);
            var headers = ea.BasicProperties.Headers;
            string? S(string k) => headers != null && headers.TryGetValue(k, out var v) ? v is byte[] b ? Encoding.UTF8.GetString(b) : v?.ToString() : null;

            // The message sat in the broker: record that wait as a span with its real start, then handle under a consume span.
            var publishedAt = LabTelemetry.ParseTime(S("published-at"));
            var parent = publishedAt is { } p ? LabTelemetry.RecordWait("queue wait " + queue, message.TraceParent, p, ActivityKind.Internal) : message.TraceParent;
            using var span = LabTelemetry.StartChildOf("consume " + queue, ActivityKind.Consumer, parent);
            span?.SetTag("messaging.system", "rabbitmq");
            span?.SetTag("messaging.destination.name", queue);
            span?.SetTag("messaging.message.id", message.MessageId);
            span?.SetTag("dclab.message.type", message.Type);
            span.TagOrder(message);
            span?.SetTag("dclab.message.origin", message.Origin);
            try
            {
                await handler(message with { TraceParent = span?.Id ?? message.TraceParent }, CancellationToken.None);
                await ch.BasicAckAsync(ea.DeliveryTag, false);
                var direction = queue == Topology.NewSideQueue ? "legacy_to_new" : "new_to_legacy";
                if (message.EnqueuedAt is { } t)
                    LabTelemetry.Lag.Record((DateTime.UtcNow - t).TotalSeconds, new("direction", direction), new("type", message.Type));
                LabTelemetry.Messages.Add(1, new("queue", queue), new("result", "ok"));
            }
            catch (Exception ex)
            {
                span?.SetStatus(ActivityStatusCode.Error, ex.Message);
                var dead = await RetryOrDeadLetterAsync(queue, ea, ex);
                await ch.BasicAckAsync(ea.DeliveryTag, false);   // the message now lives in the retry or dead-letter queue
                LabTelemetry.Messages.Add(1, new("queue", queue), new("result", dead ? "dead_letter" : "retry"));
            }
        };
        await ch.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        return new Subscription(ch);
    }

    /// <summary>Publish before ack: if we crash in between, the original is redelivered (a duplicate the inbox absorbs).</summary>
    private async Task<bool> RetryOrDeadLetterAsync(string queue, BasicDeliverEventArgs ea, Exception error)
    {
        var attempt = ea.BasicProperties.Headers is { } h && h.TryGetValue(AttemptHeader, out var a) ? Convert.ToInt32(a) : 1;
        var headers = new Dictionary<string, object?>(ea.BasicProperties.Headers ?? new Dictionary<string, object?>())
        {
            [AttemptHeader] = attempt >= _options.MaxAttempts ? attempt : attempt + 1,   // deliveries so far in the DLQ; the next delivery's number in the retry queue
            [ErrorHeader] = error.GetType().Name + ": " + error.Message,
        };
        var props = new BasicProperties
        {
            MessageId = ea.BasicProperties.MessageId, Type = ea.BasicProperties.Type, ContentType = ea.BasicProperties.ContentType,
            DeliveryMode = DeliveryModes.Persistent, Headers = headers,
        };
        var dead = attempt >= _options.MaxAttempts;
        if (dead)
            await _publish.BasicPublishAsync(Topology.DeadLetterExchange, queue, mandatory: true, props, ea.Body);
        else
            await _publish.BasicPublishAsync("", RetryQueue(queue), mandatory: true, props, ea.Body);
        return dead;
    }

    private static Message ToMessage(BasicDeliverEventArgs ea) => ToMessage(ea.BasicProperties, ea.Body);

    private static Message ToMessage(IReadOnlyBasicProperties props, ReadOnlyMemory<byte> body)
    {
        object? H(string k) => props.Headers is { } h && h.TryGetValue(k, out var v) ? v : null;
        static string? S(object? o) => o switch { byte[] b => Encoding.UTF8.GetString(b), null => null, _ => o.ToString() };
        return new Message(props.MessageId ?? "", props.Type ?? "",
            Convert.ToInt64(H("aggregate-id")), Convert.ToInt64(H("version")), S(H("origin")) ?? "",
            body.ToArray(), S(H("traceparent")), S(H("new-id")), S(H("command-id")), LabTelemetry.ParseTime(S(H("enqueued-at"))));
    }

    public async ValueTask DisposeAsync()
    {
        await _publish.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private sealed class Subscription(IChannel ch) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await ch.CloseAsync(); await ch.DisposeAsync(); }
    }
}
