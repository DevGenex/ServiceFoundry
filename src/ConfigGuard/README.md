# ServiceFoundry.ConfigGuard

Strict configuration contracts for .NET — catch misconfigured environments at startup instead of at runtime. Enforce required keys, reject unknown keys, issue deprecation warnings on renamed settings, and fail fast before the app serves a single request.

## Why use ConfigGuard?

Most .NET apps silently swallow missing or misspelled config keys — `options.ApiKey` is just an empty string and the app crashes 30 seconds later with an unhelpful NullReferenceException. ConfigGuard moves that failure to startup with a clear, actionable error message listing every violated rule.

**Typical scenario:** A deployment pipeline promotes a container image to staging. The new `Payments:StripeKey` setting was not added to the environment. Without ConfigGuard the pod starts, passes health checks, and fails on the first payment request. With ConfigGuard the pod refuses to start and the deployment pipeline fails immediately with *"Required key 'Payments:StripeKey' is missing."*

## Packages

| Package | NuGet | Use case |
|---|---|---|
| `ServiceFoundry.ConfigGuard` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ConfigGuard)](https://www.nuget.org/packages/ServiceFoundry.ConfigGuard) | Core contract DSL and validation engine. Use this in libraries or when you manage the host yourself. |
| `ServiceFoundry.ConfigGuard.Hosting` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ConfigGuard.Hosting)](https://www.nuget.org/packages/ServiceFoundry.ConfigGuard.Hosting) | Wires validation into `IHostBuilder` so it runs automatically during `Build()`. This is the package most app projects need. |

## Install

```shell
dotnet add package ServiceFoundry.ConfigGuard
dotnet add package ServiceFoundry.ConfigGuard.Hosting
```

## Getting started

Define your options class and register a contract:

```csharp
public sealed class PaymentsOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
}

// In Program.cs
builder.Services
    .AddConfigContract<PaymentsOptions>("Payments", contract =>
    {
        contract.Require(o => o.ApiKey);
        contract.Require(o => o.BaseUrl);
        contract.RejectUnknownKeys();   // catches typos in appsettings.json
    })
    .FailFastOnStartup();  // throws ConfigValidationException before the app takes a request
```

### Named options — multi-tenant or multi-provider scenarios

When you run multiple instances of the same options class (e.g. one Stripe account per tenant, or several upstream APIs with the same shape), use named contracts:

```csharp
builder.Services.AddNamedConfigContract<PaymentsOptions>("Stripe", "Payments:Stripe", contract =>
{
    contract.Require(o => o.ApiKey);
});

builder.Services.AddNamedConfigContract<PaymentsOptions>("PayPal", "Payments:PayPal", contract =>
{
    contract.Require(o => o.ApiKey);
    contract.Alias(o => o.BaseUrl, "Url", deprecated: true);  // warn when old key is present
});

// Resolve by name anywhere in the app
var stripeOptions = optionsMonitor.Get("Stripe");
```

### Nested collection strictness

Reject unknown keys inside repeated sections (e.g. a list of upstream endpoints):

```csharp
contract.RejectUnknownKeysInCollection("Upstreams");  // flags Upstreams:0:Typo
```

## Package details

### `ServiceFoundry.ConfigGuard`

The core engine. Exposes the `IConfigContract<T>` DSL with `Require`, `Alias`, `RejectUnknownKeys`, `Validate` (cross-field rules), and `Build()`. Returns a list of `ConfigDiagnostic` records — each with a severity (`Error`, `Warning`), a code (`CG001`…), and a human-readable message. Suitable for use in any .NET Standard 2.0+ project including libraries.

### `ServiceFoundry.ConfigGuard.Hosting`

Builds on top of the core package and integrates with the .NET Generic Host. `AddConfigContract<T>(...).FailFastOnStartup()` registers a hosted service that runs all contracts during `IHostedService.StartAsync`. The host will not transition to the running state if any contract returns an error-severity diagnostic. Validation results are also available via `IConfigValidationResultAccessor` for health-check endpoints.

## Delivery guarantees

- Validation is performed against raw `IConfiguration` keys — presence is checked, not CLR default values.
- Unknown-key detection is wildcard-aware for nested collection items (`Section:*:Property`).
- `FailFastOnStartup` runs inside `IHostedService.StartAsync` — the host will not accept requests if validation fails.
- Reload-time re-validation is **not** currently supported; validation runs once on startup and on demand.

## Features

- Required member enforcement
- Unknown-key rejection (full section and nested collections)
- Key aliases with optional deprecation warnings
- Named options via `IOptionsMonitor<T>.Get(name)`
- Cross-field validation rules
- Typed `ConfigDiagnostic` results with severity and code

See `samples/ConfigGuard/README.md` for additional examples.
