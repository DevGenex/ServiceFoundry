using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceFoundry.ReliableMessages.EFCore;

public interface IReliableMessagesDbContext
{
    DbSet<InboxMessageEntity> InboxMessages { get; }

    DbSet<OutboxMessageEntity> OutboxMessages { get; }
}

public sealed class OutboxMessageEntity
{
    public int AttemptCount { get; set; }

    public string Body { get; set; } = string.Empty;

    public string? CausationId { get; set; }

    public string? CorrelationId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; set; }

    public string Destination { get; set; } = string.Empty;

    public DateTimeOffset? DispatchedAtUtc { get; set; }

    public string HeadersJson { get; set; } = "{}";

    public Guid Id { get; set; }

    public string? LastError { get; set; }

    public string? LeaseId { get; set; }

    public DateTimeOffset? LeaseUntilUtc { get; set; }

    public Guid MessageId { get; set; }

    public string MessageName { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public OutboxStatus Status { get; set; }

    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }

    public DateTimeOffset VisibleAtUtc { get; set; }
}

public sealed class InboxMessageEntity
{
    public int AttemptCount { get; set; }

    public string Body { get; set; } = string.Empty;

    public string? CausationId { get; set; }

    public string ConsumerName { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; set; }

    public DateTimeOffset FirstSeenAtUtc { get; set; }

    public string HeadersJson { get; set; } = "{}";

    public Guid Id { get; set; }

    public string? LastError { get; set; }

    public string? LeaseId { get; set; }

    public DateTimeOffset? LeaseUntilUtc { get; set; }

    public Guid MessageId { get; set; }

    public string MessageName { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public InboxStatus Status { get; set; }

    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }

    public DateTimeOffset VisibleAtUtc { get; set; }
}

public static class ReliableMessagesModelBuilderExtensions
{
    public static ModelBuilder ApplyReliableMessagesModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.ToTable("ReliableMessagesOutbox");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.MessageName).IsRequired();
            entity.Property(row => row.MessageType).IsRequired();
            entity.Property(row => row.Destination).IsRequired();
            entity.Property(row => row.Body).IsRequired();
            entity.Property(row => row.HeadersJson).IsRequired();
            entity.HasIndex(row => new { row.Status, row.VisibleAtUtc });
        });

        modelBuilder.Entity<InboxMessageEntity>(entity =>
        {
            entity.ToTable("ReliableMessagesInbox");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ConsumerName).IsRequired();
            entity.Property(row => row.MessageName).IsRequired();
            entity.Property(row => row.MessageType).IsRequired();
            entity.Property(row => row.Body).IsRequired();
            entity.Property(row => row.HeadersJson).IsRequired();
            entity.HasIndex(row => new { row.ConsumerName, row.MessageId }).IsUnique();
        });

        return modelBuilder;
    }
}

public static class ReliableMessagesBuilderExtensions
{
    public static ReliableMessagesBuilder UseEntityFrameworkStore<TDbContext>(this ReliableMessagesBuilder builder)
        where TDbContext : DbContext, IReliableMessagesDbContext
    {
        builder.Services.AddSingleton<EntityFrameworkReliableMessagesStore<TDbContext>>();
        builder.Services.AddSingleton<IOutboxStore>(static serviceProvider => serviceProvider.GetRequiredService<EntityFrameworkReliableMessagesStore<TDbContext>>());
        builder.Services.AddSingleton<IInboxStore>(static serviceProvider => serviceProvider.GetRequiredService<EntityFrameworkReliableMessagesStore<TDbContext>>());
        return builder;
    }
}

public static class ReliableMessagesDbContextExtensions
{
    private static readonly IMessageSerializer Serializer = new SystemTextJsonMessageSerializer();

    public static async Task<int> SaveChangesAndEnqueueAsync<TDbContext, TMessage>(
        this TDbContext dbContext,
        TMessage message,
        CancellationToken cancellationToken = default)
        where TDbContext : DbContext, IReliableMessagesDbContext
        where TMessage : class, IMessage
    {
        return await SaveChangesAndEnqueueAsync<TDbContext, TMessage>(dbContext, message, configure: null, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> SaveChangesAndEnqueueAsync<TDbContext, TMessage>(
        this TDbContext dbContext,
        TMessage message,
        Action<MessagePublishOptions>? configure,
        CancellationToken cancellationToken = default)
        where TDbContext : DbContext, IReliableMessagesDbContext
        where TMessage : class, IMessage
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(message);

        var options = new MessagePublishOptions();
        configure?.Invoke(options);
        var envelope = MessageEnvelope<TMessage>.Create(message, options);

        dbContext.OutboxMessages.Add(new OutboxMessageEntity
        {
            AttemptCount = 0,
            Body = Serializer.Serialize(envelope.Message),
            CausationId = envelope.CausationId,
            CorrelationId = envelope.CorrelationId,
            CreatedAtUtc = envelope.OccurredAtUtc,
            Destination = envelope.Destination,
            HeadersJson = Serializer.SerializeHeaders(envelope.Headers),
            Id = Guid.NewGuid(),
            MessageId = envelope.MessageId,
            MessageName = envelope.MessageName,
            MessageType = typeof(TMessage).AssemblyQualifiedName ?? typeof(TMessage).FullName ?? typeof(TMessage).Name,
            Status = OutboxStatus.Pending,
            TraceParent = envelope.TraceParent,
            TraceState = envelope.TraceState,
            VisibleAtUtc = options.VisibleAtUtc ?? envelope.OccurredAtUtc,
        });

        return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class EntityFrameworkReliableMessagesStore<TDbContext> : IOutboxStore, IInboxStore
    where TDbContext : DbContext, IReliableMessagesDbContext
{
    private readonly IDbContextFactory<TDbContext> _contextFactory;

    public EntityFrameworkReliableMessagesStore(IDbContextFactory<TDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.OutboxMessages.Add(Map(message));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<InboxAcquireResult> AcquireAsync(InboxMessage message, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await context.InboxMessages
            .SingleOrDefaultAsync(row => row.ConsumerName == message.ConsumerName && row.MessageId == message.MessageId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var entity = Map(message);
            entity.Status = InboxStatus.Processing;
            entity.LeaseId = Guid.NewGuid().ToString("N");
            entity.LeaseUntilUtc = now.Add(leaseDuration);
            entity.VisibleAtUtc = now;

            context.InboxMessages.Add(entity);

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return InboxAcquireResult.Acquired(Map(entity));
            }
            catch (DbUpdateException)
            {
                existing = await context.InboxMessages
                    .SingleAsync(row => row.ConsumerName == message.ConsumerName && row.MessageId == message.MessageId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return await AcquireExistingAsync(context, existing, now, leaseDuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(int batchSize, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await context.OutboxMessages
            .Where(row =>
                row.Status == OutboxStatus.Pending || row.Status == OutboxStatus.Dispatching)
            .Take(batchSize * 4)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimed = candidates
            .OrderBy(row => row.CreatedAtUtc)
            .Where(row => row.VisibleAtUtc <= now && (row.LeaseUntilUtc is null || row.LeaseUntilUtc < now))
            .Take(batchSize)
            .ToList();

        foreach (var candidate in claimed)
        {
            candidate.Status = OutboxStatus.Dispatching;
            candidate.LeaseId = Guid.NewGuid().ToString("N");
            candidate.LeaseUntilUtc = now.Add(leaseDuration);
        }

        if (claimed.Count == 0)
        {
            return Array.Empty<OutboxMessage>();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return claimed.Select(Map).ToArray();
    }

    public async Task MarkCompletedAsync(Guid inboxId, int attemptCount, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.InboxMessages
            .Where(row => row.Id == inboxId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AttemptCount, attemptCount)
                .SetProperty(row => row.Status, InboxStatus.Completed)
                .SetProperty(row => row.CompletedAtUtc, completedAtUtc)
                .SetProperty(row => row.LeaseId, (string?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkDeadLetterAsync(Guid outboxId, int attemptCount, string error, DateTimeOffset deadLetteredAtUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.OutboxMessages
            .Where(row => row.Id == outboxId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AttemptCount, attemptCount)
                .SetProperty(row => row.Status, OutboxStatus.DeadLetter)
                .SetProperty(row => row.LastError, error)
                .SetProperty(row => row.DeadLetteredAtUtc, deadLetteredAtUtc)
                .SetProperty(row => row.LeaseId, (string?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    async Task IInboxStore.MarkDeadLetterAsync(Guid inboxId, int attemptCount, string error, DateTimeOffset deadLetteredAtUtc, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.InboxMessages
            .Where(row => row.Id == inboxId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AttemptCount, attemptCount)
                .SetProperty(row => row.Status, InboxStatus.DeadLetter)
                .SetProperty(row => row.LastError, error)
                .SetProperty(row => row.DeadLetteredAtUtc, deadLetteredAtUtc)
                .SetProperty(row => row.LeaseId, (string?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkDispatchedAsync(Guid outboxId, int attemptCount, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.OutboxMessages
            .Where(row => row.Id == outboxId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AttemptCount, attemptCount)
                .SetProperty(row => row.Status, OutboxStatus.Succeeded)
                .SetProperty(row => row.DispatchedAtUtc, dispatchedAtUtc)
                .SetProperty(row => row.LeaseId, (string?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PurgeCompletedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.InboxMessages
            .Where(row => row.Status == InboxStatus.Completed && row.CompletedAtUtc != null && row.CompletedAtUtc < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PurgeDeadLettersAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.OutboxMessages
            .Where(row => row.Status == OutboxStatus.DeadLetter && row.DeadLetteredAtUtc != null && row.DeadLetteredAtUtc < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    async Task IInboxStore.PurgeDeadLettersAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.InboxMessages
            .Where(row => row.Status == InboxStatus.DeadLetter && row.DeadLetteredAtUtc != null && row.DeadLetteredAtUtc < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PurgeSucceededAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.OutboxMessages
            .Where(row => row.Status == OutboxStatus.Succeeded && row.DispatchedAtUtc != null && row.DispatchedAtUtc < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RescheduleAsync(Guid outboxId, int attemptCount, DateTimeOffset visibleAtUtc, string error, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.OutboxMessages
            .Where(row => row.Id == outboxId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AttemptCount, attemptCount)
                .SetProperty(row => row.Status, OutboxStatus.Pending)
                .SetProperty(row => row.VisibleAtUtc, visibleAtUtc)
                .SetProperty(row => row.LastError, error)
                .SetProperty(row => row.LeaseId, (string?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    async Task IInboxStore.RescheduleAsync(Guid inboxId, int attemptCount, DateTimeOffset visibleAtUtc, string error, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.InboxMessages
            .Where(row => row.Id == inboxId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AttemptCount, attemptCount)
                .SetProperty(row => row.Status, InboxStatus.Pending)
                .SetProperty(row => row.VisibleAtUtc, visibleAtUtc)
                .SetProperty(row => row.LastError, error)
                .SetProperty(row => row.LeaseId, (string?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    private static InboxMessageEntity Map(InboxMessage message) => new()
    {
        AttemptCount = message.AttemptCount,
        Body = message.Body,
        CausationId = message.CausationId,
        ConsumerName = message.ConsumerName,
        CorrelationId = message.CorrelationId,
        CompletedAtUtc = message.CompletedAtUtc,
        DeadLetteredAtUtc = message.DeadLetteredAtUtc,
        FirstSeenAtUtc = message.FirstSeenAtUtc,
        HeadersJson = message.HeadersJson,
        Id = message.Id,
        LastError = message.LastError,
        LeaseId = message.LeaseId,
        LeaseUntilUtc = message.LeaseUntilUtc,
        MessageId = message.MessageId,
        MessageName = message.MessageName,
        MessageType = message.MessageType,
        Status = message.Status,
        TraceParent = message.TraceParent,
        TraceState = message.TraceState,
        VisibleAtUtc = message.VisibleAtUtc,
    };

    private static InboxMessage Map(InboxMessageEntity entity) => new()
    {
        AttemptCount = entity.AttemptCount,
        Body = entity.Body,
        CausationId = entity.CausationId,
        ConsumerName = entity.ConsumerName,
        CorrelationId = entity.CorrelationId,
        CompletedAtUtc = entity.CompletedAtUtc,
        DeadLetteredAtUtc = entity.DeadLetteredAtUtc,
        FirstSeenAtUtc = entity.FirstSeenAtUtc,
        HeadersJson = entity.HeadersJson,
        Id = entity.Id,
        LastError = entity.LastError,
        LeaseId = entity.LeaseId,
        LeaseUntilUtc = entity.LeaseUntilUtc,
        MessageId = entity.MessageId,
        MessageName = entity.MessageName,
        MessageType = entity.MessageType,
        Status = entity.Status,
        TraceParent = entity.TraceParent,
        TraceState = entity.TraceState,
        VisibleAtUtc = entity.VisibleAtUtc,
    };

    private static OutboxMessageEntity Map(OutboxMessage message) => new()
    {
        AttemptCount = message.AttemptCount,
        Body = message.Body,
        CausationId = message.CausationId,
        CorrelationId = message.CorrelationId,
        CreatedAtUtc = message.CreatedAtUtc,
        DeadLetteredAtUtc = message.DeadLetteredAtUtc,
        Destination = message.Destination,
        DispatchedAtUtc = message.DispatchedAtUtc,
        HeadersJson = message.HeadersJson,
        Id = message.Id,
        LastError = message.LastError,
        LeaseId = message.LeaseId,
        LeaseUntilUtc = message.LeaseUntilUtc,
        MessageId = message.MessageId,
        MessageName = message.MessageName,
        MessageType = message.MessageType,
        Status = message.Status,
        TraceParent = message.TraceParent,
        TraceState = message.TraceState,
        VisibleAtUtc = message.VisibleAtUtc,
    };

    private static OutboxMessage Map(OutboxMessageEntity entity) => new()
    {
        AttemptCount = entity.AttemptCount,
        Body = entity.Body,
        CausationId = entity.CausationId,
        CorrelationId = entity.CorrelationId,
        CreatedAtUtc = entity.CreatedAtUtc,
        DeadLetteredAtUtc = entity.DeadLetteredAtUtc,
        Destination = entity.Destination,
        DispatchedAtUtc = entity.DispatchedAtUtc,
        HeadersJson = entity.HeadersJson,
        Id = entity.Id,
        LastError = entity.LastError,
        LeaseId = entity.LeaseId,
        LeaseUntilUtc = entity.LeaseUntilUtc,
        MessageId = entity.MessageId,
        MessageName = entity.MessageName,
        MessageType = entity.MessageType,
        Status = entity.Status,
        TraceParent = entity.TraceParent,
        TraceState = entity.TraceState,
        VisibleAtUtc = entity.VisibleAtUtc,
    };

    private static async Task<InboxAcquireResult> AcquireExistingAsync(
        TDbContext context,
        InboxMessageEntity existing,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        return existing.Status switch
        {
            InboxStatus.Completed => InboxAcquireResult.Duplicate(),
            InboxStatus.DeadLetter => InboxAcquireResult.DeadLetter(),
            _ when existing.LeaseUntilUtc is not null && existing.LeaseUntilUtc >= now => InboxAcquireResult.Busy(),
            _ => await ReacquireAsync(context, existing.Id, now, leaseDuration, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async Task<InboxAcquireResult> ReacquireAsync(
        TDbContext context,
        Guid inboxId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        var updatedRows = await context.InboxMessages
            .Where(row => row.Id == inboxId && (row.Status == InboxStatus.Pending || row.Status == InboxStatus.Processing))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, InboxStatus.Processing)
                .SetProperty(row => row.LeaseId, leaseId)
                .SetProperty(row => row.LeaseUntilUtc, now.Add(leaseDuration)), cancellationToken)
            .ConfigureAwait(false);

        if (updatedRows != 1)
        {
            return InboxAcquireResult.Busy();
        }

        var entity = await context.InboxMessages.SingleAsync(row => row.Id == inboxId, cancellationToken).ConfigureAwait(false);
        return InboxAcquireResult.Acquired(Map(entity));
    }
}