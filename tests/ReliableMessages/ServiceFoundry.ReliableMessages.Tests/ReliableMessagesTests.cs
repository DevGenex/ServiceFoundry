using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ServiceFoundry.ReliableMessages.Tests;

public sealed class ReliableMessagesTests
{
    [Fact]
    public async Task Publisher_enqueues_message_with_metadata()
    {
        var store = new FakeOutboxStore();
        var serializer = new SystemTextJsonMessageSerializer();
        var publisher = new MessagePublisher(store, serializer, new FixedTimeProvider(new DateTimeOffset(2026, 05, 21, 12, 00, 00, TimeSpan.Zero)));

        using var activity = new Activity("publish-test");
        activity.Start();

        var options = new MessagePublishOptions
        {
            CorrelationId = "corr-1",
            CausationId = "cause-1",
            Destination = "orders",
        };
        options.Headers.Add("tenant", "acme");

        await publisher.EnqueueAsync(new OrderPlaced("order-1", 42m), options);

        var message = Assert.Single(store.Messages);
        Assert.Equal("orders", message.Destination);
        Assert.Equal("corr-1", message.CorrelationId);
        Assert.Equal(activity.Id, message.TraceParent);
        Assert.Contains("tenant", message.HeadersJson);
    }

    [Fact]
    public async Task Dispatcher_reschedules_transient_failures_with_backoff()
    {
        var now = new DateTimeOffset(2026, 05, 21, 12, 00, 00, TimeSpan.Zero);
        var store = new FakeOutboxStore(new OutboxMessage
        {
            Body = "{}",
            CreatedAtUtc = now,
            Destination = "orders",
            HeadersJson = "{}",
            MessageId = Guid.NewGuid(),
            MessageName = "OrderPlaced",
            MessageType = typeof(OrderPlaced).FullName!,
            VisibleAtUtc = now,
        });
        var dispatcher = new OutboxDispatcher(
            store,
            new StubTransport(DispatchResult.TransientFailure("temporary")),
            new SystemTextJsonMessageSerializer(),
            new ExponentialBackoffRetryPolicy(Options.Create(new RetryPolicyOptions { InitialDelay = TimeSpan.FromSeconds(2), MaxAttempts = 5 })),
            new FixedTimeProvider(now),
            Options.Create(new ReliableMessagesOptions { BatchSize = 1, LeaseDuration = TimeSpan.FromSeconds(30) }),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.DispatchDueMessagesAsync();

        Assert.Single(store.Rescheduled);
        Assert.Equal(1, store.Rescheduled[0].AttemptCount);
        Assert.Equal(now.AddSeconds(2), store.Rescheduled[0].VisibleAtUtc);
    }

    [Fact]
    public async Task Dispatcher_deadletters_permanent_failures()
    {
        var now = new DateTimeOffset(2026, 05, 21, 12, 00, 00, TimeSpan.Zero);
        var store = new FakeOutboxStore(new OutboxMessage
        {
            Body = "{}",
            CreatedAtUtc = now,
            Destination = "orders",
            HeadersJson = "{}",
            MessageId = Guid.NewGuid(),
            MessageName = "OrderPlaced",
            MessageType = typeof(OrderPlaced).FullName!,
            VisibleAtUtc = now,
        });
        var dispatcher = new OutboxDispatcher(
            store,
            new StubTransport(DispatchResult.PermanentFailure("invalid payload")),
            new SystemTextJsonMessageSerializer(),
            new ExponentialBackoffRetryPolicy(Options.Create(new RetryPolicyOptions())),
            new FixedTimeProvider(now),
            Options.Create(new ReliableMessagesOptions { BatchSize = 1, LeaseDuration = TimeSpan.FromSeconds(30) }),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.DispatchDueMessagesAsync();

        Assert.Single(store.DeadLettered);
        Assert.Equal(1, store.DeadLettered[0].AttemptCount);
    }

    [Fact]
    public async Task Inbox_processor_deduplicates_completed_messages()
    {
        var now = new DateTimeOffset(2026, 05, 21, 12, 00, 00, TimeSpan.Zero);
        var envelope = MessageEnvelope<OrderPlaced>.Create(new OrderPlaced("order-1", 42m), new MessagePublishOptions(), new FixedTimeProvider(now));
        var store = new FakeInboxStore();
        var processor = new InboxProcessor(
            store,
            new SystemTextJsonMessageSerializer(),
            new ExponentialBackoffRetryPolicy(Options.Create(new RetryPolicyOptions())),
            new FixedTimeProvider(now),
            Options.Create(new ReliableMessagesOptions { LeaseDuration = TimeSpan.FromSeconds(30) }),
            NullLogger<InboxProcessor>.Instance);

        var firstResult = await processor.ProcessAsync("billing", envelope, new RecordingHandler());
        var secondResult = await processor.ProcessAsync("billing", envelope, new RecordingHandler());

        Assert.Equal(InboxProcessResult.Handled, firstResult);
        Assert.Equal(InboxProcessResult.Duplicate, secondResult);
    }

    [Fact]
    public async Task Inbox_processor_deadletters_permanent_failures()
    {
        var now = new DateTimeOffset(2026, 05, 21, 12, 00, 00, TimeSpan.Zero);
        var envelope = MessageEnvelope<OrderPlaced>.Create(new OrderPlaced("order-1", 42m), new MessagePublishOptions(), new FixedTimeProvider(now));
        var store = new FakeInboxStore();
        var processor = new InboxProcessor(
            store,
            new SystemTextJsonMessageSerializer(),
            new ExponentialBackoffRetryPolicy(Options.Create(new RetryPolicyOptions { MaxAttempts = 2 })),
            new FixedTimeProvider(now),
            Options.Create(new ReliableMessagesOptions { LeaseDuration = TimeSpan.FromSeconds(30) }),
            NullLogger<InboxProcessor>.Instance);

        var result = await processor.ProcessAsync("billing", envelope, new PermanentFailureHandler());

        Assert.Equal(InboxProcessResult.Poisoned, result);
        Assert.Single(store.DeadLettered);
        Assert.Equal(1, store.DeadLettered[0].AttemptCount);
    }

    public sealed record OrderPlaced(string OrderId, decimal Total) : IMessage;

    private sealed class RecordingHandler : IMessageHandler<OrderPlaced>
    {
        public Task Handle(OrderPlaced message, MessageContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PermanentFailureHandler : IMessageHandler<OrderPlaced>
    {
        public Task Handle(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
            => throw new PermanentMessageFailureException("bad message");
    }

    private sealed class StubTransport : IMessageTransport
    {
        private readonly DispatchResult _result;

        public StubTransport(DispatchResult result)
        {
            _result = result;
        }

        public Task<DispatchResult> PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FakeOutboxStore : IOutboxStore
    {
        public FakeOutboxStore(params OutboxMessage[] seededMessages)
        {
            Messages = seededMessages.ToList();
        }

        public List<OutboxMessage> DeadLettered { get; } = new();

        public List<OutboxMessage> Messages { get; }

        public List<OutboxMessage> Rescheduled { get; } = new();

        public Task AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(int batchSize, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OutboxMessage>>(Messages.Where(message => message.VisibleAtUtc <= now && message.Status == OutboxStatus.Pending).Take(batchSize).ToArray());

        public Task MarkDeadLetterAsync(Guid outboxId, int attemptCount, string error, DateTimeOffset deadLetteredAtUtc, CancellationToken cancellationToken = default)
        {
            var existing = Messages.Single(message => message.Id == outboxId);
            DeadLettered.Add(existing with { AttemptCount = attemptCount, LastError = error, DeadLetteredAtUtc = deadLetteredAtUtc, Status = OutboxStatus.DeadLetter });
            return Task.CompletedTask;
        }

        public Task MarkDispatchedAsync(Guid outboxId, int attemptCount, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PurgeDeadLettersAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PurgeSucceededAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RescheduleAsync(Guid outboxId, int attemptCount, DateTimeOffset visibleAtUtc, string error, CancellationToken cancellationToken = default)
        {
            var existing = Messages.Single(message => message.Id == outboxId);
            Rescheduled.Add(existing with { AttemptCount = attemptCount, LastError = error, VisibleAtUtc = visibleAtUtc, Status = OutboxStatus.Pending });
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInboxStore : IInboxStore
    {
        private readonly Dictionary<(string ConsumerName, Guid MessageId), InboxMessage> _messages = new();

        public List<InboxMessage> DeadLettered { get; } = new();

        public Task<InboxAcquireResult> AcquireAsync(InboxMessage message, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            var key = (message.ConsumerName, message.MessageId);
            if (!_messages.TryGetValue(key, out var existing))
            {
                existing = message with { Status = InboxStatus.Processing, LeaseUntilUtc = now.Add(leaseDuration) };
                _messages[key] = existing;
                return Task.FromResult(InboxAcquireResult.Acquired(existing));
            }

            return Task.FromResult(existing.Status switch
            {
                InboxStatus.Completed => InboxAcquireResult.Duplicate(),
                InboxStatus.DeadLetter => InboxAcquireResult.DeadLetter(),
                _ => InboxAcquireResult.Acquired(existing with { Status = InboxStatus.Processing, LeaseUntilUtc = now.Add(leaseDuration) }),
            });
        }

        public Task MarkCompletedAsync(Guid inboxId, int attemptCount, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default)
        {
            var key = _messages.Single(pair => pair.Value.Id == inboxId).Key;
            _messages[key] = _messages[key] with { AttemptCount = attemptCount, CompletedAtUtc = completedAtUtc, Status = InboxStatus.Completed };
            return Task.CompletedTask;
        }

        public Task MarkDeadLetterAsync(Guid inboxId, int attemptCount, string error, DateTimeOffset deadLetteredAtUtc, CancellationToken cancellationToken = default)
        {
            var key = _messages.Single(pair => pair.Value.Id == inboxId).Key;
            _messages[key] = _messages[key] with { AttemptCount = attemptCount, DeadLetteredAtUtc = deadLetteredAtUtc, LastError = error, Status = InboxStatus.DeadLetter };
            DeadLettered.Add(_messages[key]);
            return Task.CompletedTask;
        }

        public Task PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PurgeDeadLettersAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RescheduleAsync(Guid inboxId, int attemptCount, DateTimeOffset visibleAtUtc, string error, CancellationToken cancellationToken = default)
        {
            var key = _messages.Single(pair => pair.Value.Id == inboxId).Key;
            _messages[key] = _messages[key] with { AttemptCount = attemptCount, LastError = error, Status = InboxStatus.Pending, VisibleAtUtc = visibleAtUtc };
            return Task.CompletedTask;
        }
    }
}