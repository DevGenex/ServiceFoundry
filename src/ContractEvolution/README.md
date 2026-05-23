# ServiceFoundry.ContractEvolution

Versioned contract evolution for .NET — register API or message contract versions, resolve multi-hop upgrade paths, assess compatibility, and catch unsafe maps before compile time with the included Roslyn analyzer.

## Why use ContractEvolution?

Maintaining multiple versions of an API or event contract is painful. The typical approaches — big `if/switch` blocks in controllers, hand-rolled mapper classes, or just breaking old consumers — all have serious drawbacks. ContractEvolution gives you a typed DSL to declare the upgrade path between versions once, and resolves multi-hop chains automatically at runtime.

**Typical scenario:** Your REST API has been live for two years. `v1` clients still exist in the wild. `v2` added a `Currency` field. `v3` renamed `CustomerId` to `BuyerId` and split `Address` into `ShippingAddress` / `BillingAddress`. Instead of three separate controller actions or three serialization branches, you register three contract versions and their maps. Any incoming `v1` or `v2` payload is automatically upgraded to `v3` before your handler runs.

## Packages

| Package | NuGet | Use case |
|---|---|---|
| `ServiceFoundry.ContractEvolution` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ContractEvolution)](https://www.nuget.org/packages/ServiceFoundry.ContractEvolution) | Core registry, engine, and upgrade DSL. Start here. |
| `ServiceFoundry.ContractEvolution.AspNetCore` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ContractEvolution.AspNetCore)](https://www.nuget.org/packages/ServiceFoundry.ContractEvolution.AspNetCore) | Middleware that reads the incoming version header and upgrades the request body transparently before it reaches your controller. |
| `ServiceFoundry.ContractEvolution.Testing` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ContractEvolution.Testing)](https://www.nuget.org/packages/ServiceFoundry.ContractEvolution.Testing) | xUnit assertion helpers: assert all fields are mapped, round-trips are lossless, and no bindings are missing. |
| `ServiceFoundry.ContractEvolution.Reporting` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ContractEvolution.Reporting)](https://www.nuget.org/packages/ServiceFoundry.ContractEvolution.Reporting) | Generates text and JSON reports of all registered mappings — useful for audits, API docs, and onboarding. |
| `ServiceFoundry.ContractEvolution.Analyzers` | [![NuGet](https://img.shields.io/nuget/v/ServiceFoundry.ContractEvolution.Analyzers)](https://www.nuget.org/packages/ServiceFoundry.ContractEvolution.Analyzers) | Roslyn analyzer (SFCE001) — turns missing target-member bindings into compile-time warnings. Development dependency only. |

## Install

```shell
dotnet add package ServiceFoundry.ContractEvolution
dotnet add package ServiceFoundry.ContractEvolution.Analyzers  # analyzer, development-only
```

## Getting started

```csharp
// 1. Register contract versions and their upgrade maps
builder.Services.AddContractEvolution(evolution =>
{
    evolution.ForContract<OrderContract>(contract =>
    {
        contract.Version<OrderV1>("v1");
        contract.Latest<OrderV2>("v2");

        contract.Map<OrderV1, OrderV2>("v1", "v2", map =>
        {
            // Same-named properties are copied automatically.
            // Provide explicit rules for additions, renames, and computed values.
            map.Default(o => o.Currency, "USD");
        });
    });
});

// 2. Upgrade at runtime
var result = engine.Upgrade<OrderContract, OrderV2>(incomingPayload, sourceVersion: "v1");
var upgraded = result.Value;  // OrderV2 with Currency = "USD"
```

The `MissingTargetBindingAnalyzer` (SFCE001) will warn at build time if `Currency` above were omitted from the map entirely.

## Package details

### `ServiceFoundry.ContractEvolution`

The core engine. `ContractEvolutionBuilder` holds the version registry and compiles it into an immutable `IContractEvolutionEngine` at `Build()` time. Path resolution uses BFS over the registered `Map` edges, so `v1 → v3` resolves automatically via `v1 → v2 → v3` without you writing a direct map. Graph validation (cycles, ambiguous paths, missing bindings) runs at `Build()` time — before the app starts.

### `ServiceFoundry.ContractEvolution.AspNetCore`

Adds request-pipeline middleware that reads a configurable version header (default: `X-Contract-Version`), deserialises the request body as the declared source version, upgrades it to the latest version using the engine, and re-populates the request body before your controller reads it. Your controllers always see the latest contract type regardless of what version the client sent.

### `ServiceFoundry.ContractEvolution.Testing`

xUnit-friendly assertion extensions. `ShouldMapAllFields<TSource, TTarget>()` verifies that every property on the target type has a binding. `ShouldRoundTrip<TSource, TTarget, TSource>()` verifies that upgrading and then downgrading produces an equivalent source object. Catches breaking contract changes in CI before they reach consumers.

### `ServiceFoundry.ContractEvolution.Reporting`

Access via `IContractEvolutionReportProvider` (registered automatically with `AddContractEvolution`). Call `GetReport()` for a structured `ContractEvolutionReport` object, or use the text/JSON writers to dump the full mapping inventory to a file or an API endpoint. Useful for API changelog generation and security audits.

### `ServiceFoundry.ContractEvolution.Analyzers`

A Roslyn source analyzer (diagnostic ID **SFCE001**). At compile time it inspects every `Map<TSource, TTarget>` call and emits a warning for each property on `TTarget` that has no explicit binding and whose name does not match a property on `TSource` (i.e. it would not be auto-copied). Catches the "I added a field but forgot to map it" class of bugs without running any tests.

## Runtime behaviour when no path exists

If `ResolvePlan` or `Upgrade` is called for a source/target pair with no registered upgrade path, a `ContractEvolutionValidationException` is thrown containing the list of diagnostics. If the registration itself is invalid (e.g. a cycle or an ambiguous path), the exception is thrown at `Build()` time, before the app starts.

## Features

- Typed DSL: `Version`, `Latest`, `Map`, `Copy`, `Rename`, `Default`, `Compute`
- Multi-hop BFS path resolution
- Compatibility assessment: `Compatible`, `Upgradeable`, `Breaking`
- Graph validation at `Build()` time (cycles, ambiguous paths, missing bindings)
- Text and JSON report writers via `ContractEvolution.Reporting`
- **SFCE001** Roslyn analyzer — warns on unmapped target properties at compile time
- ASP.NET Core request-body upgrade hook

See `samples/ContractEvolution/README.md` for additional examples.
