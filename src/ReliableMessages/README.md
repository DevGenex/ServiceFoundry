# ServiceFoundry.ReliableMessages

Pragmatic outbox/inbox runtime for .NET services — durable at-least-once publish, inbox deduplication, retry, dead-letter, and pluggable transport. No external coordinator required.

## Why use ReliableMessages?

The classic dual-write problem: you save an `Order` to your database and then publish an `OrderPlaced` event to RabbitMQ. If the process crashes between the two, either the order is saved but no event is published, or — with a reversed order — an event fires for an order that was never persisted. ReliableMessages solves this with the outbox pattern: the message is written to the database **in the same transaction** as your domain data, and a background worker delivers it afterwards.

**Typical scenario:** An e-commerce service places orders and publishes `OrderPlaced` events for downstream fulfilment and notification services. `SaveChangesAndEnqueueAsync` ensures that if the database commits, the event will eventually be delivered — even if the pod restarts immediately after.

## Packages

| Package | NuGet | Use case |
|---|---|---|
| `ServiceFoundry.ReliableMessages` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ReliableMessages)](https://www.nuget.org/packages/ServiceFoundry.ReliableMessages) | Core abstractions, outbox/inbox runtime, and background dispatcher. Required by all other packages. |
| `ServiceFoundry.ReliableMessages.EFCore` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ReliableMessages.EFCore)](https://www.nuget.org/packages/ServiceFoundry.ReliableMessages.EFCore) | Persists the outbox in your existing EF Core `DbContext`. Provides `SaveChangesAndEnqueueAsync` for atomic enqueue. |
| `ServiceFoundry.ReliableMessages.RabbitMQ` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ReliableMessages.RabbitMQ)](https://www.nuget.org/packages/ServiceFoundry.ReliableMessages.RabbitMQ) | Publishes outbox messages to a RabbitMQ topic exchange with automatic reconnect on dropped connections. |

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

### Adding RabbitMQ transport

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

## Package details

### `ServiceFoundry.ReliableMessages`

The core runtime. Defines `IOutboxMessage`, `IMessageHandler<T>`, `IOutboxPublisher`, and the background `OutboxDispatcher` hosted service. Transport-agnostic — wire up any `IMessageTransport` implementation. Includes OpenTelemetry `ActivitySource` and `Meter` for distributed tracing and metrics out of the box.

### `ServiceFoundry.ReliableMessages.EFCore`

Adds an `OutboxMessage` entity to your existing `DbContext` (via owned entity or a separate table) and provides `SaveChangesAndEnqueueAsync`, which writes your domain changes and the outbox row in a single `IDbContextTransaction`. No second database connection, no distributed transaction — just one commit.

### `ServiceFoundry.ReliableMessages.RabbitMQ`

Implements `IMessageTransport` over `RabbitMQ.Client`. Publishes to a topic exchange with full `trace-id` / `causation-id` header propagation. The underlying AMQP connection and channel are lazily created and transparently recreated if the broker drops the connection — your outbox dispatcher keeps running through transient RabbitMQ restarts.

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
