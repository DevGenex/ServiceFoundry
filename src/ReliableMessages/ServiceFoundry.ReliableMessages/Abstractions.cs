using System.Diagnostics;

namespace ServiceFoundry.ReliableMessages;

public interface IMessage
{
}

public interface IMessageHandler<in TMessage> where TMessage : class, IMessage
{
    Task Handle(TMessage message, MessageContext context, CancellationToken cancellationToken);
}

public interface IMessagePublisher
{
    Task EnqueueAsync<TMessage>(TMessage message, MessagePublishOptions? options = null, CancellationToken cancellationToken = default)
        where TMessage : class, IMessage;
}

public interface IMessageSerializer
{
    string Serialize<TMessage>(TMessage message) where TMessage : class, IMessage;

    TMessage Deserialize<TMessage>(string body) where TMessage : class, IMessage;

    string SerializeHeaders(MessageHeaders headers);

    MessageHeaders DeserializeHeaders(string payload);
}

public interface IMessageTransport
{
    Task<DispatchResult> PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default);
}

public interface IOutboxStore
{
    Task AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(int batchSize, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    Task MarkDispatchedAsync(Guid outboxId, int attemptCount, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken = default);

    Task RescheduleAsync(Guid outboxId, int attemptCount, DateTimeOffset visibleAtUtc, string error, CancellationToken cancellationToken = default);

    Task MarkDeadLetterAsync(Guid outboxId, int attemptCount, string error, DateTimeOffset deadLetteredAtUtc, CancellationToken cancellationToken = default);

    Task PurgeSucceededAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default);

    Task PurgeDeadLettersAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default);
}

public interface IInboxStore
{
    Task<InboxAcquireResult> AcquireAsync(InboxMessage message, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(Guid inboxId, int attemptCount, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default);

    Task RescheduleAsync(Guid inboxId, int attemptCount, DateTimeOffset visibleAtUtc, string error, CancellationToken cancellationToken = default);

    Task MarkDeadLetterAsync(Guid inboxId, int attemptCount, string error, DateTimeOffset deadLetteredAtUtc, CancellationToken cancellationToken = default);

    Task PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default);

    Task PurgeDeadLettersAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default);
}

public interface IRetryPolicy
{
    TimeSpan? GetNextDelay(int attemptCount, FailureClassification classification);
}

public interface IInboxProcessor
{
    Task<InboxProcessResult> ProcessAsync<TMessage>(
        string consumerName,
        MessageEnvelope<TMessage> envelope,
        IMessageHandler<TMessage> handler,
        CancellationToken cancellationToken = default)
        where TMessage : class, IMessage;
}

public interface IReliableMessagesMaintenance
{
    Task PurgeAsync(CancellationToken cancellationToken = default);
}

public sealed class MessageHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, string> _values;

    public MessageHeaders()
        : this(Enumerable.Empty<KeyValuePair<string, string>>())
    {
    }

    public MessageHeaders(IEnumerable<KeyValuePair<string, string>> values)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            _values[pair.Key] = pair.Value;
        }
    }

    public string this[string key] => _values[key];

    public int Count => _values.Count;

    public void Add(string key, string value) => _values[key] = value;

    public Dictionary<string, string> ToDictionary() => new(_values, StringComparer.OrdinalIgnoreCase);

    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class MessagePublishOptions
{
    public string? CausationId { get; set; }

    public string? CorrelationId { get; set; }

    public string? Destination { get; set; }

    public MessageHeaders Headers { get; } = new();

    public Guid? MessageId { get; set; }

    public string? MessageName { get; set; }

    public DateTimeOffset? VisibleAtUtc { get; set; }
}

public sealed record MessageEnvelope<TMessage>(
    Guid MessageId,
    TMessage Message,
    string MessageName,
    string Destination,
    MessageHeaders Headers,
    string? CorrelationId,
    string? CausationId,
    string? TraceParent,
    string? TraceState,
    DateTimeOffset OccurredAtUtc)
    where TMessage : class, IMessage
{
    public static MessageEnvelope<TMessage> Create(TMessage message, MessagePublishOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        var resolvedOptions = options ?? new MessagePublishOptions();
        var activity = Activity.Current;
        var headers = new MessageHeaders(resolvedOptions.Headers);

        return new MessageEnvelope<TMessage>(
            resolvedOptions.MessageId ?? Guid.NewGuid(),
            message,
            string.IsNullOrWhiteSpace(resolvedOptions.MessageName) ? typeof(TMessage).Name : resolvedOptions.MessageName,
            string.IsNullOrWhiteSpace(resolvedOptions.Destination) ? typeof(TMessage).Name : resolvedOptions.Destination,
            headers,
            resolvedOptions.CorrelationId,
            resolvedOptions.CausationId,
            activity?.Id,
            activity?.TraceStateString,
            (timeProvider ?? TimeProvider.System).GetUtcNow());
    }
}

public sealed record MessageContext(
    string ConsumerName,
    Guid MessageId,
    string MessageName,
    int AttemptCount,
    MessageHeaders Headers,
    string? CorrelationId,
    string? CausationId,
    string? TraceParent,
    string? TraceState,
    DateTimeOffset OccurredAtUtc);

public sealed record OutboxMessage
{
    public int AttemptCount { get; init; }

    public string Body { get; init; } = string.Empty;

    public string? CausationId { get; init; }

    public string? CorrelationId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? DeadLetteredAtUtc { get; init; }

    public string Destination { get; init; } = string.Empty;

    public DateTimeOffset? DispatchedAtUtc { get; init; }

    public string HeadersJson { get; init; } = "{}";

    public Guid Id { get; init; } = Guid.NewGuid();

    public string? LastError { get; init; }

    public string? LeaseId { get; init; }

    public DateTimeOffset? LeaseUntilUtc { get; init; }

    public Guid MessageId { get; init; }

    public string MessageName { get; init; } = string.Empty;

    public string MessageType { get; init; } = string.Empty;

    public OutboxStatus Status { get; init; } = OutboxStatus.Pending;

    public string? TraceParent { get; init; }

    public string? TraceState { get; init; }

    public DateTimeOffset VisibleAtUtc { get; init; }
}

public sealed record InboxMessage
{
    public int AttemptCount { get; init; }

    public string Body { get; init; } = string.Empty;

    public string? CausationId { get; init; }

    public string ConsumerName { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? DeadLetteredAtUtc { get; init; }

    public DateTimeOffset FirstSeenAtUtc { get; init; }

    public string HeadersJson { get; init; } = "{}";

    public Guid Id { get; init; } = Guid.NewGuid();

    public string? LastError { get; init; }

    public string? LeaseId { get; init; }

    public DateTimeOffset? LeaseUntilUtc { get; init; }

    public Guid MessageId { get; init; }

    public string MessageName { get; init; } = string.Empty;

    public string MessageType { get; init; } = string.Empty;

    public InboxStatus Status { get; init; } = InboxStatus.Pending;

    public string? TraceParent { get; init; }

    public string? TraceState { get; init; }

    public DateTimeOffset VisibleAtUtc { get; init; }
}

public sealed record InboxAcquireResult(InboxAcquireStatus Status, InboxMessage? Message)
{
    public static InboxAcquireResult Acquired(InboxMessage message) => new(InboxAcquireStatus.Acquired, message);

    public static InboxAcquireResult Busy() => new(InboxAcquireStatus.Busy, null);

    public static InboxAcquireResult DeadLetter() => new(InboxAcquireStatus.DeadLetter, null);

    public static InboxAcquireResult Duplicate() => new(InboxAcquireStatus.Duplicate, null);
}

public enum InboxAcquireStatus
{
    Acquired,
    Busy,
    DeadLetter,
    Duplicate,
}

public enum InboxProcessResult
{
    Handled,
    Duplicate,
    Retry,
    Poisoned,
}

public enum FailureClassification
{
    Transient,
    Permanent,
}

public enum OutboxStatus
{
    Pending,
    Dispatching,
    Succeeded,
    DeadLetter,
}

public enum InboxStatus
{
    Pending,
    Processing,
    Completed,
    DeadLetter,
}

public enum DispatchOutcome
{
    Succeeded,
    TransientFailure,
    PermanentFailure,
}

public sealed record DispatchResult(DispatchOutcome Outcome, string? Reason = null)
{
    public static DispatchResult PermanentFailure(string? reason = null) => new(DispatchOutcome.PermanentFailure, reason);

    public static DispatchResult Success() => new(DispatchOutcome.Succeeded);

    public static DispatchResult TransientFailure(string? reason = null) => new(DispatchOutcome.TransientFailure, reason);
}

public sealed record OutgoingMessage(
    Guid MessageId,
    string MessageName,
    string Destination,
    string Body,
    MessageHeaders Headers,
    string? CorrelationId,
    string? CausationId,
    string? TraceParent,
    string? TraceState,
    DateTimeOffset OccurredAtUtc);

public sealed class ReliableMessagesOptions
{
    public int BatchSize { get; set; } = 25;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(12);

    public TimeSpan CompletedInboxRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan CompletedOutboxRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan DeadLetterRetention { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class RetryPolicyOptions
{
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public double Multiplier { get; set; } = 2.0d;

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    public int MaxAttempts { get; set; } = 5;
}

public sealed class PermanentMessageFailureException : Exception
{
    public PermanentMessageFailureException(string message)
        : base(message)
    {
    }

    public PermanentMessageFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}