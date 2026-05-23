using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ServiceFoundry.ConfigGuard.Hosting;

namespace ServiceFoundry.ConfigGuard.Hosting.Tests;

public sealed class ConfigContractHostingTests
{
    [Fact]
    public async Task Fail_fast_validation_throws_on_host_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:BaseUrl"] = "https://api.example.com",
        });

        builder.Services.AddConfigContract<PaymentsOptions>("Payments", contract =>
        {
            contract.Require(options => options.ApiKey);
            contract.Require(options => options.BaseUrl);
        }).FailFastOnStartup();

        using var host = builder.Build();

        await Assert.ThrowsAsync<ConfigValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void Options_binding_resolves_aliases_for_existing_options_consumers()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:ApiToken"] = "legacy-token",
            ["Payments:BaseUrl"] = "https://api.example.com",
        });

        builder.Services.AddConfigContract<PaymentsOptions>("Payments", contract =>
        {
            contract.Require(options => options.ApiKey);
            contract.Require(options => options.BaseUrl);
            contract.Alias("ApiToken", options => options.ApiKey, "Use Payments:ApiKey instead.");
        });

        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<PaymentsOptions>>().Value;
        Assert.Equal("legacy-token", options.ApiKey);
    }

    [Fact]
    public void Named_options_can_be_resolved_through_options_monitor()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Stripe:ApiToken"] = "stripe-token",
            ["Payments:Stripe:BaseUrl"] = "https://stripe.example.com",
            ["Payments:PayPal:ApiKey"] = "paypal-token",
            ["Payments:PayPal:BaseUrl"] = "https://paypal.example.com",
        });

        builder.Services.AddNamedConfigContract<PaymentsOptions>("Stripe", "Payments:Stripe", contract =>
        {
            contract.Require(options => options.ApiKey);
            contract.Require(options => options.BaseUrl);
            contract.Alias("ApiToken", options => options.ApiKey, "Use Payments:Stripe:ApiKey instead.");
        });

        builder.Services.AddNamedConfigContract<PaymentsOptions>("PayPal", "Payments:PayPal", contract =>
        {
            contract.Require(options => options.ApiKey);
            contract.Require(options => options.BaseUrl);
        });

        using var host = builder.Build();

        var monitor = host.Services.GetRequiredService<IOptionsMonitor<PaymentsOptions>>();
        Assert.Equal("stripe-token", monitor.Get("Stripe").ApiKey);
        Assert.Equal("paypal-token", monitor.Get("PayPal").ApiKey);
    }

    [Fact]
    public async Task Named_options_fail_fast_validation_throws_on_host_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Stripe:BaseUrl"] = "https://stripe.example.com",
        });

        builder.Services.AddNamedConfigContract<PaymentsOptions>("Stripe", "Payments:Stripe", contract =>
        {
            contract.Require(options => options.ApiKey);
            contract.Require(options => options.BaseUrl);
        }).FailFastOnStartup();

        using var host = builder.Build();

        await Assert.ThrowsAsync<ConfigValidationException>(() => host.StartAsync());
    }

    public sealed class PaymentsOptions
    {
        public string? ApiKey { get; set; }

        public string? BaseUrl { get; set; }
    }
}