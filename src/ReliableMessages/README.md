# ServiceFoundry.ReliableMessages

Pragmatic outbox/inbox runtime for .NET services — durable at-least-once publish, inbox deduplication, retry, dead-letter, and pluggable transport. No external coordinator required.

## Packages

| Package | Purpose |
|---|---|
| `ServiceFoundry.ReliableMessages` | Core outbox/inbox runtime and abstractions |
| `ServiceFoundry.ReliableMessages.EFCore` | EF Core persistence and same-transaction enqueue |
| `ServiceFoundry.ReliableMessages.RabbitMQ` | RabbitMQ transport |

## Install

```shell
dotnet add package ServiceFoundry.ReliableMessages
dotnet add package ServiceFoundry.ReliableMessages.EFCore
```

## Getting started

```csharp
// 1. Register
builder.Services
    .AddReliableMessages(options => { options.BatchSize = 50; })
    .UseEntityFramework<AppDbContext>()
    .AddDispatcher();

// 2. Enqueue inside your EF Core save — the message is written atomically with your domain data
public async Task PlaceOrder(Order order)
{
    _db.Orders.Add(order);
    await _db.SaveChangesAndEnqueueAsync(
        _publisher.PrepareMessage(new OrderPlaced(order.Id)), cancellationToken);
}

// 3. Handle on the other side
public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken ct)
        => Console.Out.WriteLineAsync($"Order {message.OrderId} placed");
}
```

With RabbitMQ transport:

```csharp
builder.Services
    .AddReliableMessages()
    .UseRabbitMqTransport(mq =>
    {
        mq.Uri = "amqp://guest:guest@localhost:5672/";
        mq.ExchangeName = "orders";
    })
    .AddDispatcher();
```

## Delivery guarantees

- **At-least-once delivery.** Messages are retried until they are successfully dispatched or exhaust the configured retry limit, at which point they are dead-lettered.
- **Inbox deduplication.** The `InboxProcessor` stores message IDs and rejects duplicates within the configured deduplication window. This provides at-most-once handler execution per unique message ID.
- **Transactional enqueue** (EFCore only). `SaveChangesAndEnqueueAsync` writes the message row in the same database transaction as the caller's domain changes — no dual-write, no phantom messages.
- **Reconnect** (RabbitMQ). The RabbitMQ publisher transparently recreates dropped connections and channels before each publish attempt.

## Features

- Durable outbox with configurable batch size, lock duration, and retry backoff
- Inbox deduplication and poison-message dead-lettering
- EF Core persistence model and `SaveChangesAndEnqueueAsync`
- RabbitMQ topic exchange publishing with full trace/causation header propagation
- OpenTelemetry `ActivitySource` and `Meter` instrumentation
- Cleanup hooks for completed and dead-lettered records

See `samples/ReliableMessages/README.md` for additional examples.
