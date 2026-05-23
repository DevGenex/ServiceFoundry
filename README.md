# ServiceFoundry

ServiceFoundry is a .NET-first monorepo for three independent boundary-focused products:

- `ServiceFoundry.ConfigGuard`: strict configuration contracts with required fields, unknown-key detection, aliases, deprecation warnings, named options support, nested collection diagnostics, and host startup validation.
- `ServiceFoundry.ReliableMessages`: durable outbox publishing, inbox deduplication, retry and dead-letter handling, EF Core integration for same-save-path enqueueing, and a RabbitMQ transport package.
- `ServiceFoundry.ContractEvolution`: explicit contract version registration, multi-hop upgrade paths, compatibility assessment, an ASP.NET Core request upgrade hook, reporting helpers, and Roslyn analyzer tooling.

## Repo Layout

```text
src/
	ConfigGuard/
	ReliableMessages/
	ContractEvolution/
tests/
	ConfigGuard/
	ReliableMessages/
	ContractEvolution/
samples/
	ConfigGuard/
	ReliableMessages/
	ContractEvolution/
docs/
```

## Build And Test

```powershell
dotnet test ServiceFoundry.slnx
```

## Product Entry Points

- ConfigGuard: `src/ConfigGuard/ServiceFoundry.ConfigGuard` and `src/ConfigGuard/ServiceFoundry.ConfigGuard.Hosting`
- ReliableMessages: `src/ReliableMessages/ServiceFoundry.ReliableMessages`, `src/ReliableMessages/ServiceFoundry.ReliableMessages.EFCore`, and `src/ReliableMessages/ServiceFoundry.ReliableMessages.RabbitMQ`
- ContractEvolution: `src/ContractEvolution/ServiceFoundry.ContractEvolution`, `src/ContractEvolution/ServiceFoundry.ContractEvolution.AspNetCore`, `src/ContractEvolution/ServiceFoundry.ContractEvolution.Testing`, `src/ContractEvolution/ServiceFoundry.ContractEvolution.Reporting`, and `src/ContractEvolution/ServiceFoundry.ContractEvolution.Analyzers`

## Current Scope

- ConfigGuard ships the strict contract validator, diagnostics, named options support, nested collection diagnostics, and the `IOptions<T>` bridge.
- ReliableMessages ships the core runtime, EF Core persistence, and a RabbitMQ transport package.
- ContractEvolution ships the core registry/runtime, test helpers, reporting helpers, an analyzer package, and a request-body upgrader for ASP.NET Core.

See `docs/roadmap.md` for milestone notes and package boundaries.