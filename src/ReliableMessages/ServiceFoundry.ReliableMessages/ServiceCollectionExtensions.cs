using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ServiceFoundry.ReliableMessages;

public static class ReliableMessagesServiceCollectionExtensions
{
    public static ReliableMessagesBuilder AddReliableMessages(
        this IServiceCollection services,
        Action<ReliableMessagesOptions>? configure = null,
        Action<RetryPolicyOptions>? configureRetry = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ReliableMessagesOptions>();
        services.AddOptions<RetryPolicyOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        if (configureRetry is not null)
        {
            services.Configure(configureRetry);
        }

        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IMessageSerializer, SystemTextJsonMessageSerializer>();
        services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
        services.AddSingleton<IMessagePublisher, MessagePublisher>();
        services.AddSingleton<IInboxProcessor, InboxProcessor>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<IReliableMessagesMaintenance, ReliableMessagesMaintenance>();

        return new ReliableMessagesBuilder(services);
    }
}

public sealed class ReliableMessagesBuilder
{
    internal ReliableMessagesBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    public ReliableMessagesBuilder AddCleanupWorker()
    {
        Services.AddSingleton<IHostedService, ReliableMessagesCleanupService>();
        return this;
    }

    public ReliableMessagesBuilder AddDispatcher()
    {
        Services.AddSingleton<IHostedService, ReliableMessagesDispatcherService>();
        return this;
    }

    public ReliableMessagesBuilder UseTransport<TTransport>() where TTransport : class, IMessageTransport
    {
        Services.AddSingleton<IMessageTransport, TTransport>();
        return this;
    }
}