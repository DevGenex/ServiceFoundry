using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ServiceFoundry.ReliableMessages.RabbitMQ;

public sealed class RabbitMqTransportOptions
{
    public string? ClientProvidedName { get; set; }

    public bool DeclareExchange { get; set; } = true;

    public string ExchangeName { get; set; } = "servicefoundry.reliablemessages";

    public string ExchangeType { get; set; } = global::RabbitMQ.Client.ExchangeType.Topic;

    public bool Mandatory { get; set; }

    public bool PersistentDelivery { get; set; } = true;

    public Func<OutgoingMessage, string>? RoutingKeyResolver { get; set; }

    public string? Uri { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Uri))
        {
            throw new InvalidOperationException("RabbitMQ transport requires a connection Uri.");
        }

        if (string.IsNullOrWhiteSpace(ExchangeName))
        {
            throw new InvalidOperationException("RabbitMQ transport requires an exchange name.");
        }

        if (string.IsNullOrWhiteSpace(ExchangeType))
        {
            throw new InvalidOperationException("RabbitMQ transport requires an exchange type.");
        }
    }
}

internal sealed class RabbitMqMessageTransport : IMessageTransport
{
    private readonly IRabbitMqPublisher _publisher;
    private readonly RabbitMqTransportOptions _options;

    public RabbitMqMessageTransport(IRabbitMqPublisher publisher, IOptions<RabbitMqTransportOptions> options)
    {
        _publisher = publisher;
        _options = options.Value;
        _options.Validate();
    }

    public async Task<DispatchResult> PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            await _publisher.PublishAsync(BuildPublishRequest(message), cancellationToken).ConfigureAwait(false);
            return DispatchResult.Success();
        }
        catch (BrokerUnreachableException exception)
        {
            return DispatchResult.TransientFailure(exception.Message);
        }
        catch (AlreadyClosedException exception)
        {
            return DispatchResult.TransientFailure(exception.Message);
        }
        catch (OperationInterruptedException exception)
        {
            return DispatchResult.PermanentFailure(exception.Message);
        }
    }

    internal RabbitMqPublishRequest BuildPublishRequest(OutgoingMessage message)
    {
        var routingKey = _options.RoutingKeyResolver?.Invoke(message)
            ?? message.Destination;

        var headers = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Headers)
        {
            headers[header.Key] = Encoding.UTF8.GetBytes(header.Value);
        }

        headers["servicefoundry-message-name"] = Encoding.UTF8.GetBytes(message.MessageName);
        headers["servicefoundry-occurred-at-utc"] = Encoding.UTF8.GetBytes(message.OccurredAtUtc.ToString("O"));

        if (!string.IsNullOrWhiteSpace(message.TraceParent))
        {
            headers["traceparent"] = Encoding.UTF8.GetBytes(message.TraceParent);
        }

        if (!string.IsNullOrWhiteSpace(message.TraceState))
        {
            headers["tracestate"] = Encoding.UTF8.GetBytes(message.TraceState);
        }

        if (!string.IsNullOrWhiteSpace(message.CausationId))
        {
            headers["servicefoundry-causation-id"] = Encoding.UTF8.GetBytes(message.CausationId);
        }

        return new RabbitMqPublishRequest(
            _options.ExchangeName,
            routingKey,
            _options.ExchangeType,
            _options.DeclareExchange,
            _options.Mandatory,
            _options.PersistentDelivery,
            message.MessageId,
            message.MessageName,
            message.Body,
            headers,
            message.CorrelationId,
            message.TraceParent,
            message.TraceState,
            message.OccurredAtUtc);
    }
}

public static class RabbitMqReliableMessagesBuilderExtensions
{
    public static ReliableMessagesBuilder UseRabbitMqTransport(
        this ReliableMessagesBuilder builder,
        Action<RabbitMqTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<RabbitMqTransportOptions>().Configure(configure);
        builder.Services.AddSingleton<IRabbitMqPublisher, RabbitMqClientPublisher>();
        builder.Services.AddSingleton<IMessageTransport, RabbitMqMessageTransport>();
        return builder;
    }
}

internal sealed record RabbitMqPublishRequest(
    string ExchangeName,
    string RoutingKey,
    string ExchangeType,
    bool DeclareExchange,
    bool Mandatory,
    bool PersistentDelivery,
    Guid MessageId,
    string MessageName,
    string Body,
    IReadOnlyDictionary<string, byte[]> Headers,
    string? CorrelationId,
    string? TraceParent,
    string? TraceState,
    DateTimeOffset OccurredAtUtc);

internal interface IRabbitMqPublisher
{
    Task PublishAsync(RabbitMqPublishRequest request, CancellationToken cancellationToken = default);
}

internal sealed class RabbitMqClientPublisher : IRabbitMqPublisher, IDisposable
{
    private readonly object _gate = new();
    private readonly RabbitMqTransportOptions _options;
    private IConnection? _connection;
    private IModel? _channel;
    private bool _exchangeDeclared;

    public RabbitMqClientPublisher(IOptions<RabbitMqTransportOptions> options)
    {
        _options = options.Value;
        _options.Validate();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisposeChannelLocked();
            DisposeConnectionLocked();
        }
    }

    public Task PublishAsync(RabbitMqPublishRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = GetOrRecreateChannel();
        EnsureExchange(channel, request);

        var properties = channel.CreateBasicProperties();
        properties.Persistent = request.PersistentDelivery;
        properties.MessageId = request.MessageId.ToString("D");
        properties.Type = request.MessageName;
        properties.ContentType = "application/json";
        properties.ContentEncoding = "utf-8";
        properties.Timestamp = new AmqpTimestamp(request.OccurredAtUtc.ToUnixTimeSeconds());
        properties.CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? request.MessageId.ToString("D")
            : request.CorrelationId;
        properties.Headers = request.Headers.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.OrdinalIgnoreCase);

        channel.BasicPublish(
            request.ExchangeName,
            request.RoutingKey,
            request.Mandatory,
            properties,
            Encoding.UTF8.GetBytes(request.Body));

        return Task.CompletedTask;
    }

    private void EnsureExchange(IModel channel, RabbitMqPublishRequest request)
    {
        if (!request.DeclareExchange || _exchangeDeclared)
        {
            return;
        }

        lock (_gate)
        {
            if (_exchangeDeclared)
            {
                return;
            }

            channel.ExchangeDeclare(request.ExchangeName, request.ExchangeType, durable: true, autoDelete: false);
            _exchangeDeclared = true;
        }
    }

    /// <summary>
    /// Returns the current channel, recreating it (and the underlying connection if needed)
    /// whenever either has been closed or aborted. Thread-safe via <see cref="_gate"/>.
    /// </summary>
    private IModel GetOrRecreateChannel()
    {
        lock (_gate)
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            // Channel is closed or was never created — dispose and rebuild from connection.
            DisposeChannelLocked();

            if (_connection is not { IsOpen: true })
            {
                DisposeConnectionLocked();
                _connection = CreateConnectionCore();
            }

            _channel = _connection.CreateModel();
            // Force exchange re-declaration after a reconnect so the topology is guaranteed.
            _exchangeDeclared = false;
            return _channel;
        }
    }

    private void DisposeChannelLocked()
    {
        try { _channel?.Dispose(); } catch { /* best-effort */ }
        _channel = null;
    }

    private void DisposeConnectionLocked()
    {
        try { _connection?.Dispose(); } catch { /* best-effort */ }
        _connection = null;
    }

    private IConnection CreateConnectionCore()
    {
        var connectionFactory = new ConnectionFactory
        {
            DispatchConsumersAsync = true,
            Uri = new Uri(_options.Uri!, UriKind.Absolute),
        };

        return string.IsNullOrWhiteSpace(_options.ClientProvidedName)
            ? connectionFactory.CreateConnection()
            : connectionFactory.CreateConnection(_options.ClientProvidedName);
    }
}