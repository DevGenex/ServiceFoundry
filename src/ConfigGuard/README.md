# ServiceFoundry.ConfigGuard

Strict configuration contracts for .NET — required keys, unknown-key rejection, aliases with deprecation warnings, named options, and fail-fast startup validation.

## Packages

| Package | Purpose |
|---|---|
| `ServiceFoundry.ConfigGuard` | Core contract and validation engine |
| `ServiceFoundry.ConfigGuard.Hosting` | `IOptions<T>` bridge and host startup validation |

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

// In Program.cs / Startup
builder.Services
    .AddConfigContract<PaymentsOptions>("Payments", contract =>
    {
        contract.Require(o => o.ApiKey);
        contract.Require(o => o.BaseUrl);
        contract.RejectUnknownKeys();
    })
    .FailFastOnStartup();  // throws ConfigValidationException before the app takes a request
```

Add named options for multi-tenant or multi-provider scenarios:

```csharp
builder.Services.AddNamedConfigContract<PaymentsOptions>("Stripe", "Payments:Stripe", contract =>
{
    contract.Require(o => o.ApiKey);
});

// Resolve
var stripeOptions = optionsMonitor.Get("Stripe");
```

## Delivery guarantees

- Validation is performed against `IConfiguration` keys — key presence is checked, not CLR default values.
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
