using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceFoundry.ReliableMessages;

public sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public SystemTextJsonMessageSerializer(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _jsonSerializerOptions = jsonSerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public TMessage Deserialize<TMessage>(string body) where TMessage : class, IMessage
        => JsonSerializer.Deserialize<TMessage>(body, _jsonSerializerOptions)
           ?? throw new InvalidOperationException($"Unable to deserialize '{typeof(TMessage).Name}'.");

    public MessageHeaders DeserializeHeaders(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new MessageHeaders();
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, _jsonSerializerOptions)
                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new MessageHeaders(values);
    }

    public string Serialize<TMessage>(TMessage message) where TMessage : class, IMessage
        => JsonSerializer.Serialize(message, _jsonSerializerOptions);

    public string SerializeHeaders(MessageHeaders headers)
        => JsonSerializer.Serialize(headers.ToDictionary(), _jsonSerializerOptions);
}

public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly RetryPolicyOptions _options;

    public ExponentialBackoffRetryPolicy(IOptions<RetryPolicyOptions> options)
    {
        _options = options.Value;
    }

    public TimeSpan? GetNextDelay(int attemptCount, FailureClassification classification)
    {
        if (classification == FailureClassification.Permanent || attemptCount >= _options.MaxAttempts)
        {
            return null;
        }

        var exponent = Math.Max(attemptCount - 1, 0);
        var nextDelay = TimeSpan.FromMilliseconds(_options.InitialDelay.TotalMilliseconds * Math.Pow(_options.Multiplier, exponent));
        return nextDelay > _options.MaxDelay ? _options.MaxDelay : nextDelay;
    }
}

public sealed class MessagePublisher : IMessagePublisher
{
    private readonly IMessageSerializer _serializer;
    private readonly IOutboxStore _store;
    private readonly TimeProvider _timeProvider;

    public MessagePublisher(IOutboxStore store, IMessageSerializer serializer, TimeProvider timeProvider)
    {
        _store = store;
        _serializer = serializer;
        _timeProvider = timeProvider;
    }

    public async Task EnqueueAsync<TMessage>(TMessage message, MessagePublishOptions? options = null, CancellationToken cancellationToken = default)
        where TMessage : class, IMessage
    {
        var envelope = MessageEnvelope<TMessage>.Create(message, options, _timeProvider);
        var outboxMessage = new OutboxMessage
        {
            AttemptCount = 0,
            Body = _serializer.Serialize(envelope.Message),
            CausationId = envelope.CausationId,
            CorrelationId = envelope.CorrelationId,
            CreatedAtUtc = envelope.OccurredAtUtc,
            Destination = envelope.Destination,
            HeadersJson = _serializer.SerializeHeaders(envelope.Headers),
            MessageId = envelope.MessageId,
            MessageName = envelope.MessageName,
            MessageType = typeof(TMessage).AssemblyQualifiedName ?? typeof(TMessage).FullName ?? typeof(TMessage).Name,
            TraceParent = envelope.TraceParent,
            TraceState = envelope.TraceState,
            VisibleAtUtc = options?.VisibleAtUtc ?? envelope.OccurredAtUtc,
        };

        await _store.AppendAsync(outboxMessage, cancellationToken).ConfigureAwait(false);
        ReliableMessagesDiagnostics.PublishedCounter.Add(1);
    }
}

public sealed class OutboxDispatcher
{
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly ReliableMessagesOptions _options;
    private readonly IRetryPolicy _retryPolicy;
    private readonly IMessageSerializer _serializer;
    private readonly IOutboxStore _store;
    private readonly IMessageTransport _transport;
    private readonly TimeProvider _timeProvider;

    public OutboxDispatcher(
        IOutboxStore store,
        IMessageTransport transport,
        IMessageSerializer serializer,
        IRetryPolicy retryPolicy,
        TimeProvider timeProvider,
        IOptions<ReliableMessagesOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        _store = store;
        _transport = transport;
        _serializer = serializer;
        _retryPolicy = retryPolicy;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> DispatchDueMessagesAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var claimedMessages = await _store.ClaimBatchAsync(_options.BatchSize, now, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);

        foreach (var message in claimedMessages)
        {
            await DispatchSingleAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return claimedMessages.Count;
    }

    private async Task DispatchSingleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = ReliableMessagesDiagnostics.ActivitySource.StartActivity("servicefoundry.outbox.dispatch", ActivityKind.Producer);
        activity?.SetTag("messaging.message_id", message.MessageId);
        activity?.SetTag("messaging.destination", message.Destination);
        activity?.SetTag("messaging.operation", "publish");

        var currentAttempt = message.AttemptCount + 1;

        try
        {
            var result = await _transport.PublishAsync(
                new OutgoingMessage(
                    message.MessageId,
                    message.MessageName,
                    message.Destination,
                    message.Body,
                    _serializer.DeserializeHeaders(message.HeadersJson),
                    message.CorrelationId,
                    message.CausationId,
                    message.TraceParent,
                    message.TraceState,
                    message.CreatedAtUtc),
                cancellationToken).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case DispatchOutcome.Succeeded:
                    await _store.MarkDispatchedAsync(message.Id, currentAttempt, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    ReliableMessagesDiagnostics.DispatchedCounter.Add(1);
                    return;
                case DispatchOutcome.PermanentFailure:
                    await _store.MarkDeadLetterAsync(message.Id, currentAttempt, result.Reason ?? "Dispatch permanently failed.", _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    ReliableMessagesDiagnostics.DeadLetterCounter.Add(1);
                    return;
                default:
                    await HandleRetryAsync(message.Id, currentAttempt, result.Reason ?? "Dispatch transiently failed.", FailureClassification.Transient, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var classification = exception is PermanentMessageFailureException
                ? FailureClassification.Permanent
                : FailureClassification.Transient;

            await HandleRetryAsync(message.Id, currentAttempt, exception.Message, classification, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(exception, "ReliableMessages dispatch failed for message {MessageId}.", message.MessageId);
        }
    }

    private async Task HandleRetryAsync(Guid outboxId, int currentAttempt, string error, FailureClassification classification, CancellationToken cancellationToken)
    {
        var delay = _retryPolicy.GetNextDelay(currentAttempt, classification);
        if (delay is null)
        {
            await _store.MarkDeadLetterAsync(outboxId, currentAttempt, error, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            ReliableMessagesDiagnostics.DeadLetterCounter.Add(1);
            return;
        }

        await _store.RescheduleAsync(outboxId, currentAttempt, _timeProvider.GetUtcNow().Add(delay.Value), error, cancellationToken).ConfigureAwait(false);
        ReliableMessagesDiagnostics.RetriedCounter.Add(1);
    }
}

public sealed class InboxProcessor : IInboxProcessor
{
    private readonly ILogger<InboxProcessor> _logger;
    private readonly ReliableMessagesOptions _options;
    private readonly IRetryPolicy _retryPolicy;
    private readonly IMessageSerializer _serializer;
    private readonly IInboxStore _store;
    private readonly TimeProvider _timeProvider;

    public InboxProcessor(
        IInboxStore store,
        IMessageSerializer serializer,
        IRetryPolicy retryPolicy,
        TimeProvider timeProvider,
        IOptions<ReliableMessagesOptions> options,
        ILogger<InboxProcessor> logger)
    {
        _store = store;
        _serializer = serializer;
        _retryPolicy = retryPolicy;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<InboxProcessResult> ProcessAsync<TMessage>(
        string consumerName,
        MessageEnvelope<TMessage> envelope,
        IMessageHandler<TMessage> handler,
        CancellationToken cancellationToken = default)
        where TMessage : class, IMessage
    {
        var now = _timeProvider.GetUtcNow();
        var inboxMessage = new InboxMessage
        {
            AttemptCount = 0,
            Body = _serializer.Serialize(envelope.Message),
            CausationId = envelope.CausationId,
            ConsumerName = consumerName,
            CorrelationId = envelope.CorrelationId,
            FirstSeenAtUtc = now,
            HeadersJson = _serializer.SerializeHeaders(envelope.Headers),
            MessageId = envelope.MessageId,
            MessageName = envelope.MessageName,
            MessageType = typeof(TMessage).AssemblyQualifiedName ?? typeof(TMessage).FullName ?? typeof(TMessage).Name,
            TraceParent = envelope.TraceParent,
            TraceState = envelope.TraceState,
            VisibleAtUtc = now,
        };

        var acquireResult = await _store.AcquireAsync(inboxMessage, now, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        switch (acquireResult.Status)
        {
            case InboxAcquireStatus.Duplicate:
                return InboxProcessResult.Duplicate;
            case InboxAcquireStatus.DeadLetter:
                return InboxProcessResult.Poisoned;
            case InboxAcquireStatus.Busy:
                return InboxProcessResult.Retry;
        }

        var acquired = acquireResult.Message ?? throw new InvalidOperationException("Inbox acquire succeeded without a message payload.");
        var currentAttempt = acquired.AttemptCount + 1;

        using var activity = ReliableMessagesDiagnostics.ActivitySource.StartActivity("servicefoundry.inbox.handle", ActivityKind.Consumer);
        activity?.SetTag("messaging.message_id", acquired.MessageId);
        activity?.SetTag("messaging.consumer", consumerName);
        activity?.SetTag("messaging.operation", "process");

        var context = new MessageContext(
            consumerName,
            envelope.MessageId,
            envelope.MessageName,
            currentAttempt,
            envelope.Headers,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.TraceParent,
            envelope.TraceState,
            envelope.OccurredAtUtc);

        try
        {
            await handler.Handle(envelope.Message, context, cancellationToken).ConfigureAwait(false);
            await _store.MarkCompletedAsync(acquired.Id, currentAttempt, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            ReliableMessagesDiagnostics.ProcessedCounter.Add(1);
            return InboxProcessResult.Handled;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var classification = exception is PermanentMessageFailureException
                ? FailureClassification.Permanent
                : FailureClassification.Transient;

            var delay = _retryPolicy.GetNextDelay(currentAttempt, classification);
            if (delay is null)
            {
                await _store.MarkDeadLetterAsync(acquired.Id, currentAttempt, exception.Message, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                ReliableMessagesDiagnostics.DeadLetterCounter.Add(1);
                return InboxProcessResult.Poisoned;
            }

            await _store.RescheduleAsync(acquired.Id, currentAttempt, _timeProvider.GetUtcNow().Add(delay.Value), exception.Message, cancellationToken).ConfigureAwait(false);
            ReliableMessagesDiagnostics.RetriedCounter.Add(1);
            _logger.LogWarning(exception, "ReliableMessages handler failed for consumer {ConsumerName} and message {MessageId}.", consumerName, envelope.MessageId);
            return InboxProcessResult.Retry;
        }
    }
}

public sealed class ReliableMessagesMaintenance : IReliableMessagesMaintenance
{
    private readonly IInboxStore _inboxStore;
    private readonly ReliableMessagesOptions _options;
    private readonly IOutboxStore _outboxStore;
    private readonly TimeProvider _timeProvider;

    public ReliableMessagesMaintenance(
        IOutboxStore outboxStore,
        IInboxStore inboxStore,
        TimeProvider timeProvider,
        IOptions<ReliableMessagesOptions> options)
    {
        _outboxStore = outboxStore;
        _inboxStore = inboxStore;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public async Task PurgeAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await _outboxStore.PurgeSucceededAsync(now - _options.CompletedOutboxRetention, cancellationToken).ConfigureAwait(false);
        await _outboxStore.PurgeDeadLettersAsync(now - _options.DeadLetterRetention, cancellationToken).ConfigureAwait(false);
        await _inboxStore.PurgeCompletedAsync(now - _options.CompletedInboxRetention, cancellationToken).ConfigureAwait(false);
        await _inboxStore.PurgeDeadLettersAsync(now - _options.DeadLetterRetention, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ReliableMessagesDispatcherService : BackgroundService
{
    private readonly OutboxDispatcher _dispatcher;
    private readonly ReliableMessagesOptions _options;

    public ReliableMessagesDispatcherService(OutboxDispatcher dispatcher, IOptions<ReliableMessagesOptions> options)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatchedCount = await _dispatcher.DispatchDueMessagesAsync(stoppingToken).ConfigureAwait(false);
            if (dispatchedCount == 0)
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

public sealed class ReliableMessagesCleanupService : BackgroundService
{
    private readonly IReliableMessagesMaintenance _maintenance;
    private readonly ReliableMessagesOptions _options;

    public ReliableMessagesCleanupService(IReliableMessagesMaintenance maintenance, IOptions<ReliableMessagesOptions> options)
    {
        _maintenance = maintenance;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _maintenance.PurgeAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(_options.CleanupInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static class ReliableMessagesDiagnostics
{
    public static readonly ActivitySource ActivitySource = new("ServiceFoundry.ReliableMessages");
    public static readonly Meter Meter = new("ServiceFoundry.ReliableMessages");
    public static readonly Counter<long> DeadLetterCounter = Meter.CreateCounter<long>("servicefoundry.reliablemessages.deadlettered");
    public static readonly Counter<long> DispatchedCounter = Meter.CreateCounter<long>("servicefoundry.reliablemessages.dispatched");
    public static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>("servicefoundry.reliablemessages.processed");
    public static readonly Counter<long> PublishedCounter = Meter.CreateCounter<long>("servicefoundry.reliablemessages.published");
    public static readonly Counter<long> RetriedCounter = Meter.CreateCounter<long>("servicefoundry.reliablemessages.retried");
}