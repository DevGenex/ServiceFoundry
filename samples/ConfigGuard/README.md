# ConfigGuard Sample

```csharp
builder.Services.AddConfigContract<PaymentsOptions>("Payments", contract =>
{
    contract.Require(options => options.ApiKey);
    contract.Require(options => options.BaseUrl);
    contract.Validate(options => options.TimeoutSeconds > 0, "TimeoutSeconds must be positive", keyPath: "TimeoutSeconds");
    contract.When(options => string.Equals(options.Provider, "Stripe", StringComparison.OrdinalIgnoreCase))
        .Require(options => options.WebhookSecret, "WebhookSecret is required when Provider is Stripe.");
    contract.Alias("ApiToken", options => options.ApiKey, "Use Payments:ApiKey instead.");
}).FailFastOnStartup();

builder.Services.AddNamedConfigContract<PaymentsOptions>("Stripe", "Payments:Stripe", contract =>
{
    contract.Require(options => options.ApiKey);
    contract.Require(options => options.BaseUrl);
});
```