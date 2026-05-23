# ServiceFoundry.ContractEvolution

Versioned contract evolution for .NET — register API or message contract versions, resolve multi-hop upgrade paths, assess compatibility, and catch unsafe maps before compile time with the included Roslyn analyzer.

## Packages

| Package | Purpose |
|---|---|
| `ServiceFoundry.ContractEvolution` | Core registry, engine, and upgrade DSL |
| `ServiceFoundry.ContractEvolution.AspNetCore` | Request version reading and body upgrade middleware |
| `ServiceFoundry.ContractEvolution.Testing` | Assertion helpers for unit and integration tests |
| `ServiceFoundry.ContractEvolution.Reporting` | Text and JSON report writers |
| `ServiceFoundry.ContractEvolution.Analyzers` | Roslyn analyzer (SFCE001) |

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
