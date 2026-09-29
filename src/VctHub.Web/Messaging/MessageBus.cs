using System.Text.Json;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace VctHub.Web.Messaging;

/// <summary>Event published when a series ends.</summary>
public record MatchFinished(int MatchId, string TeamA, string TeamB, int MapsA, int MapsB, DateTime At);

/// <summary>Event published when a series goes live.</summary>
public record MatchStarted(int MatchId, string TeamA, string TeamB, DateTime At);

public interface IMessageBus
{
    string Kind { get; }
    Task PublishAsync<T>(string routingKey, T message, CancellationToken ct = default);
    /// <summary>Starts consuming; handler gets (routingKey, json body). Throwing = message goes back to the queue.</summary>
    Task SubscribeAsync(string queue, string bindingKey, Func<string, string, Task> handler, CancellationToken ct);
}

/// <summary>
/// B8: RabbitMQ with a topic exchange "vct.events". Durable queue, manual ack, publisher confirms.
/// </summary>
public sealed class RabbitMqBus(IConfiguration config, ILogger<RabbitMqBus> log) : IMessageBus, IAsyncDisposable
{
    public const string Exchange = "vct.events";
    public string Kind => "rabbitmq";
    IConnection? connection;
    IChannel? publishChannel;
    readonly SemaphoreSlim gate = new(1, 1);

    async Task<IConnection> ConnectAsync(CancellationToken ct)
    {
        if (connection is { IsOpen: true }) return connection;
        await gate.WaitAsync(ct);
        try
        {
            if (connection is { IsOpen: true }) return connection;
            var factory = new ConnectionFactory { Uri = new Uri(config["RABBITMQ_URL"]!), AutomaticRecoveryEnabled = true, ClientProvidedName = "vct-hub" };
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    connection = await factory.CreateConnectionAsync(ct);
                    break;
                }
                catch (Exception e) when (attempt < 10)
                {
                    log.LogWarning("RabbitMQ not ready ({Msg}), retry {N}", e.Message, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }
            await using var ch = await connection.CreateChannelAsync(cancellationToken: ct);
            await ch.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
            return connection;
        }
        finally { gate.Release(); }
    }

    public async Task PublishAsync<T>(string routingKey, T message, CancellationToken ct = default)
    {
        var conn = await ConnectAsync(ct);
        publishChannel ??= await conn.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent, Type = typeof(T).Name };
        await publishChannel.BasicPublishAsync(Exchange, routingKey, mandatory: false, props, body, ct);
        log.LogInformation("Published {Key} ({Bytes} B)", routingKey, body.Length);
    }

    public async Task SubscribeAsync(string queue, string bindingKey, Func<string, string, Task> handler, CancellationToken ct)
    {
        var conn = await ConnectAsync(ct);
        var ch = await conn.CreateChannelAsync(cancellationToken: ct);
        await ch.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await ch.QueueBindAsync(queue, Exchange, bindingKey, cancellationToken: ct);
        await ch.BasicQosAsync(0, prefetchCount: 5, global: false, ct);
        var consumer = new AsyncEventingBasicConsumer(ch);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                await handler(ea.RoutingKey, System.Text.Encoding.UTF8.GetString(ea.Body.Span));
                await ch.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Handler failed for {Key}; requeue={Requeue}", ea.RoutingKey, !ea.Redelivered);
                // one retry, then drop so a poison message can't loop forever
                await ch.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: !ea.Redelivered);
            }
        };
        await ch.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        log.LogInformation("Consuming {Queue} ({Binding})", queue, bindingKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (publishChannel != null) await publishChannel.DisposeAsync();
        if (connection != null) await connection.DisposeAsync();
    }
}

/// <summary>Fallback when no broker is configured (e.g. the free Render instance): same contract, in-process queue.</summary>
public sealed class InMemoryBus(ILogger<InMemoryBus> log) : IMessageBus
{
    public string Kind => "in-memory";
    readonly Channel<(string Key, string Body)> queue = Channel.CreateUnbounded<(string, string)>();
    readonly List<(string Binding, Func<string, string, Task> Handler)> handlers = [];

    public Task PublishAsync<T>(string routingKey, T message, CancellationToken ct = default) =>
        queue.Writer.WriteAsync((routingKey, JsonSerializer.Serialize(message)), ct).AsTask();

    public Task SubscribeAsync(string q, string bindingKey, Func<string, string, Task> handler, CancellationToken ct)
    {
        lock (handlers)
        {
            handlers.Add((bindingKey, handler));
            if (handlers.Count == 1) _ = Pump(ct);
        }
        return Task.CompletedTask;
    }

    async Task Pump(CancellationToken ct)
    {
        await foreach (var (key, body) in queue.Reader.ReadAllAsync(ct))
            foreach (var (binding, handler) in handlers.ToArray())
                if (Matches(binding, key))
                    try { await handler(key, body); }
                    catch (Exception e) { log.LogWarning(e, "In-memory handler failed for {Key}", key); }
    }

    // topic matching: "match.*" / "match.#" / exact
    static bool Matches(string binding, string key) =>
        binding == "#" || binding == key ||
        (binding.EndsWith(".#") && key.StartsWith(binding[..^1])) ||
        (binding.EndsWith(".*") && key.StartsWith(binding[..^1]) && !key[(binding.Length - 1)..].Contains('.'));
}
