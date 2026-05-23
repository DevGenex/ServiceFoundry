using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServiceFoundry.ReliableMessages.EFCore;

namespace ServiceFoundry.ReliableMessages.EFCore.Tests;

public sealed class ReliableMessagesEfCoreTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<OrdersDbContext> _options = null!;

    [Fact]
    public async Task SaveChangesAndEnqueueAsync_persists_business_data_and_outbox_message()
    {
        await using var context = new OrdersDbContext(_options);
        context.Orders.Add(new OrderEntity { Id = "order-1", Total = 42m });

        await context.SaveChangesAndEnqueueAsync(new OrderPlaced("order-1", 42m));

        await using var verificationContext = new OrdersDbContext(_options);
        Assert.Single(await verificationContext.Orders.ToListAsync());
        var outbox = await verificationContext.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxStatus.Pending, outbox.Status);
        Assert.Equal("OrderPlaced", outbox.MessageName);
    }

    [Fact]
    public async Task Dispatcher_marks_outbox_message_as_succeeded()
    {
        await using (var context = new OrdersDbContext(_options))
        {
            context.Orders.Add(new OrderEntity { Id = "order-1", Total = 42m });
            await context.SaveChangesAndEnqueueAsync(new OrderPlaced("order-1", 42m));
        }

        var factory = new TestDbContextFactory<OrdersDbContext>(() => new OrdersDbContext(_options));
        var store = new EntityFrameworkReliableMessagesStore<OrdersDbContext>(factory);
        var dispatcher = new OutboxDispatcher(
            store,
            new SuccessfulTransport(),
            new SystemTextJsonMessageSerializer(),
            new ExponentialBackoffRetryPolicy(Options.Create(new RetryPolicyOptions())),
            TimeProvider.System,
            Options.Create(new ReliableMessagesOptions { BatchSize = 1, LeaseDuration = TimeSpan.FromSeconds(30) }),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.DispatchDueMessagesAsync();

        await using var verificationContext = new OrdersDbContext(_options);
        var outbox = await verificationContext.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxStatus.Succeeded, outbox.Status);
        Assert.NotNull(outbox.DispatchedAtUtc);
    }

    [Fact]
    public async Task Inbox_processor_uses_entity_framework_store_for_deduplication()
    {
        var factory = new TestDbContextFactory<OrdersDbContext>(() => new OrdersDbContext(_options));
        var store = new EntityFrameworkReliableMessagesStore<OrdersDbContext>(factory);
        var processor = new InboxProcessor(
            store,
            new SystemTextJsonMessageSerializer(),
            new ExponentialBackoffRetryPolicy(Options.Create(new RetryPolicyOptions())),
            TimeProvider.System,
            Options.Create(new ReliableMessagesOptions { LeaseDuration = TimeSpan.FromSeconds(30) }),
            NullLogger<InboxProcessor>.Instance);
        var envelope = MessageEnvelope<OrderPlaced>.Create(new OrderPlaced("order-1", 42m));

        var first = await processor.ProcessAsync("billing", envelope, new RecordingHandler());
        var second = await processor.ProcessAsync("billing", envelope, new RecordingHandler());

        Assert.Equal(InboxProcessResult.Handled, first);
        Assert.Equal(InboxProcessResult.Duplicate, second);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var context = new OrdersDbContext(_options);
        await context.Database.EnsureCreatedAsync();
    }

    public sealed record OrderPlaced(string OrderId, decimal Total) : IMessage;

    private sealed class RecordingHandler : IMessageHandler<OrderPlaced>
    {
        public Task Handle(OrderPlaced message, MessageContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SuccessfulTransport : IMessageTransport
    {
        public Task<DispatchResult> PublishAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(DispatchResult.Success());
    }

    private sealed class TestDbContextFactory<TDbContext> : IDbContextFactory<TDbContext>
        where TDbContext : DbContext
    {
        private readonly Func<TDbContext> _factory;

        public TestDbContextFactory(Func<TDbContext> factory)
        {
            _factory = factory;
        }

        public TDbContext CreateDbContext() => _factory();

        public ValueTask<TDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_factory());
    }

    private sealed class OrdersDbContext : DbContext, IReliableMessagesDbContext
    {
        public OrdersDbContext(DbContextOptions<OrdersDbContext> options)
            : base(options)
        {
        }

        public DbSet<InboxMessageEntity> InboxMessages => Set<InboxMessageEntity>();

        public DbSet<OrderEntity> Orders => Set<OrderEntity>();

        public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OrderEntity>(entity =>
            {
                entity.HasKey(order => order.Id);
            });
            modelBuilder.ApplyReliableMessagesModel();
        }
    }

    private sealed class OrderEntity
    {
        public string Id { get; set; } = string.Empty;

        public decimal Total { get; set; }
    }
}