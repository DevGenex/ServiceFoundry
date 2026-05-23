using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ServiceFoundry.ReliableMessages.RabbitMQ;

namespace ServiceFoundry.ReliableMessages.RabbitMQ.Tests;

public sealed class RabbitMqTransportTests
{
    [Fact]
    public async Task PublishAsync_maps_message_metadata_to_rabbitmq_request()
    {
        var publisher = new RecordingPublisher();
        var transport = new RabbitMqMessageTransport(
            publisher,
            Options.Create(new RabbitMqTransportOptions
            {
                ExchangeName = "orders.exchange",
                ExchangeType = "topic",
                Uri = "amqp://guest:guest@localhost:5672/",
            }));

        var message = new OutgoingMessage(
            Guid.Parse("4e930b3f-1c2a-40b8-b23e-8024773db6c7"),
            "OrderPlaced",
            "orders.created",
            "{\"orderId\":\"order-1\"}",
            new MessageHeaders(new Dictionary<string, string> { ["tenant"] = "acme" }),
            "corr-1",
            "cause-1",
            "00-abcd-01",
            "state-1",
            new DateTimeOffset(2026, 05, 21, 12, 00, 00, TimeSpan.Zero));

        var result = await transport.PublishAsync(message);

        Assert.Equal(DispatchOutcome.Succeeded, result.Outcome);
        var request = Assert.Single(publisher.Requests);
        Assert.Equal("orders.exchange", request.ExchangeName);
        Assert.Equal("orders.created", request.RoutingKey);
        Assert.Equal("OrderPlaced", request.MessageName);
        Assert.Equal("corr-1", request.CorrelationId);
        Assert.Equal("traceparent", request.Headers.Keys.Single(key => key == "traceparent"));
        Assert.Equal("acme", Encoding.UTF8.GetString(request.Headers["tenant"]));
        Assert.Equal("cause-1", Encoding.UTF8.GetString(request.Headers["servicefoundry-causation-id"]));
    }

    [Fact]
    public void UseRabbitMqTransport_registers_message_transport()
    {
        var services = new ServiceCollection();
        var builder = services.AddReliableMessages();

        builder.UseRabbitMqTransport(options =>
        {
            options.ExchangeName = "orders.exchange";
            options.ExchangeType = "topic";
            options.Uri = "amqp://guest:guest@localhost:5672/";
        });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<RabbitMqMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        var options = provider.GetRequiredService<IOptions<RabbitMqTransportOptions>>().Value;
        Assert.Equal("orders.exchange", options.ExchangeName);
    }

    [Fact]
    public async Task PublishAsync_uses_custom_routing_key_resolver_when_configured()
    {
        var publisher = new RecordingPublisher();
        var transport = new RabbitMqMessageTransport(
            publisher,
            Options.Create(new RabbitMqTransportOptions
            {
                ExchangeName = "orders.exchange",
                ExchangeType = "topic",
                Uri = "amqp://guest:guest@localhost:5672/",
                RoutingKeyResolver = outgoingMessage => $"events.{outgoingMessage.MessageName.ToLowerInvariant()}"
            }));

        await transport.PublishAsync(new OutgoingMessage(
            Guid.NewGuid(),
            "OrderPlaced",
            "ignored-destination",
            "{}",
            new MessageHeaders(),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow));

        Assert.Equal("events.orderplaced", Assert.Single(publisher.Requests).RoutingKey);
    }

    private sealed class RecordingPublisher : IRabbitMqPublisher
    {
        public List<RabbitMqPublishRequest> Requests { get; } = new();

        public Task PublishAsync(RabbitMqPublishRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}