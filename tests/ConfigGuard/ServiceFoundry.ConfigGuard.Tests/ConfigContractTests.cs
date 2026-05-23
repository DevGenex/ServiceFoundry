using Microsoft.Extensions.Configuration;

namespace ServiceFoundry.ConfigGuard.Tests;

public sealed class ConfigContractTests
{
    [Fact]
    public void Require_reports_missing_required_key()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Payments:BaseUrl"] = "https://api.example.com",
        });

        var contract = ConfigContract.Create<PaymentsOptions>("Payments", builder =>
        {
            builder.Require(options => options.ApiKey);
            builder.Require(options => options.BaseUrl);
        });

        var result = contract.Validate(configuration);

        Assert.True(result.HasErrors);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CFG001", diagnostic.Code);
        Assert.Equal("ApiKey", diagnostic.KeyPath);
    }

    [Fact]
    public void Alias_satisfies_required_key_and_binds_value()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Payments:ApiToken"] = "legacy-token",
            ["Payments:BaseUrl"] = "https://api.example.com",
        });

        var contract = ConfigContract.Create<PaymentsOptions>("Payments", builder =>
        {
            builder.Require(options => options.ApiKey);
            builder.Require(options => options.BaseUrl);
            builder.Alias("ApiToken", options => options.ApiKey, "Use Payments:ApiKey instead.");
        });

        var result = contract.Validate(configuration);

        Assert.False(result.HasErrors);
        Assert.Equal("legacy-token", result.Options.ApiKey);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("CFG003", warning.Code);
    }

    [Fact]
    public void Unknown_keys_are_reported_in_strict_mode()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Payments:ApiKey"] = "abc",
            ["Payments:BaseUrl"] = "https://api.example.com",
            ["Payments:Unexpected"] = "value",
        });

        var contract = ConfigContract.Create<PaymentsOptions>("Payments", builder =>
        {
            builder.Require(options => options.ApiKey);
            builder.Require(options => options.BaseUrl);
        });

        var result = contract.Validate(configuration);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CFG002" && diagnostic.KeyPath == "Unexpected");
    }

    [Fact]
    public void Canonical_keys_win_when_alias_is_also_present()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Payments:ApiKey"] = "canonical",
            ["Payments:ApiToken"] = "legacy",
            ["Payments:BaseUrl"] = "https://api.example.com",
        });

        var contract = ConfigContract.Create<PaymentsOptions>("Payments", builder =>
        {
            builder.Require(options => options.ApiKey);
            builder.Require(options => options.BaseUrl);
            builder.Alias("ApiToken", options => options.ApiKey, "Use Payments:ApiKey instead.");
        });

        var result = contract.Validate(configuration);

        Assert.Equal("canonical", result.Options.ApiKey);
    }

    [Fact]
    public void Conditional_requirements_and_cross_field_rules_are_aggregated()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Payments:ApiToken"] = "legacy-token",
            ["Payments:BaseUrl"] = "https://api.example.com",
            ["Payments:Provider"] = "Stripe",
            ["Payments:TimeoutSeconds"] = "0",
        });

        var contract = ConfigContract.Create<PaymentsOptions>("Payments", builder =>
        {
            builder.Require(options => options.ApiKey);
            builder.Require(options => options.BaseUrl);
            builder.Alias("ApiToken", options => options.ApiKey, "Use Payments:ApiKey instead.");
            builder.Validate(options => options.TimeoutSeconds > 0, "TimeoutSeconds must be positive", keyPath: "TimeoutSeconds");
            builder.When(options => string.Equals(options.Provider, "Stripe", StringComparison.OrdinalIgnoreCase))
                .Require(options => options.WebhookSecret, "WebhookSecret is required when Provider is Stripe.");
        });

        var result = contract.Validate(configuration);

        Assert.True(result.HasErrors);
        Assert.Equal(3, result.Diagnostics.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CFG001" && diagnostic.KeyPath == "WebhookSecret");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CFG003");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CFG004" && diagnostic.KeyPath == "TimeoutSeconds");
    }

    [Fact]
    public void Unknown_keys_inside_collection_items_are_reported_without_flagging_known_nested_members()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Payments:ApiKey"] = "abc",
            ["Payments:BaseUrl"] = "https://api.example.com",
            ["Payments:Endpoints:0:Name"] = "primary",
            ["Payments:Endpoints:0:Url"] = "https://gateway.example.com",
            ["Payments:Endpoints:0:Unexpected"] = "value",
        });

        var contract = ConfigContract.Create<PaymentsOptions>("Payments", builder =>
        {
            builder.Require(options => options.ApiKey);
            builder.Require(options => options.BaseUrl);
        });

        var result = contract.Validate(configuration);

        var diagnostic = Assert.Single(result.Diagnostics.Where(entry => entry.Code == "CFG002"));
        Assert.Equal("Endpoints:0:Unexpected", diagnostic.KeyPath);
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    public sealed class PaymentsOptions
    {
        public string? ApiKey { get; set; }

        public string? BaseUrl { get; set; }

        public List<EndpointOptions> Endpoints { get; set; } = new();

        public string? Provider { get; set; }

        public int TimeoutSeconds { get; set; }

        public string? WebhookSecret { get; set; }
    }

    public sealed class EndpointOptions
    {
        public string? Name { get; set; }

        public string? Url { get; set; }
    }
}