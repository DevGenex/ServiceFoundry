# ReliableMessages Sample

```csharp
await dbContext.SaveChangesAndEnqueueAsync(
    new OrderPlaced(orderId, totalAmount),
    cancellationToken);

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
    {
        // business logic
        return Task.CompletedTask;
    }
}

services.AddReliableMessages(options =>
    {
        options.BatchSize = 50;
    })
    .UseRabbitMqTransport(rabbitMq =>
    {
        rabbitMq.Uri = "amqp://guest:guest@localhost:5672/";
        rabbitMq.ExchangeName = "servicefoundry.orders";
    })
    .AddDispatcher();
```