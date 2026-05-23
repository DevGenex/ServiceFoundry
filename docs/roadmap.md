# Roadmap

## Package Boundaries

- `ServiceFoundry.ConfigGuard`
  - Contract definition, binding, diagnostics, validation.
- `ServiceFoundry.ConfigGuard.Hosting`
  - DI registration, startup fail-fast validation, `IOptions<T>` bridge.
- `ServiceFoundry.ReliableMessages`
  - Core abstractions, outbox dispatch, inbox processing, retry policy, maintenance jobs.
- `ServiceFoundry.ReliableMessages.EFCore`
  - EF Core entities, model configuration, durable stores, `SaveChangesAndEnqueueAsync`.
- `ServiceFoundry.ReliableMessages.RabbitMQ`
  - RabbitMQ-backed `IMessageTransport` implementation and transport registration helpers.
- `ServiceFoundry.ContractEvolution`
  - Version registry, map DSL, graph validation, multi-hop upgrade engine.
- `ServiceFoundry.ContractEvolution.AspNetCore`
  - Request version readers, request-body upgrade hook, endpoint metadata helper.
- `ServiceFoundry.ContractEvolution.Testing`
  - Test host and assertion helpers for upgrade plans.
- `ServiceFoundry.ContractEvolution.Reporting`
  - Text and JSON report writers over the engine report-provider surface.
- `ServiceFoundry.ContractEvolution.Analyzers`
  - Roslyn analyzer rules that catch unsafe maps before runtime.

## Milestone 1

- Ship the current strict/core/runtime implementations.
- Keep `ReliableMessages` focused on the EF Core durable-store path before adding broker packages.
- Keep `ContractEvolution` focused on inbound upgrades before any downgrade pipeline.

## Milestone 2

- ConfigGuard: add optional reload-time validation and broader dictionary/key-shape diagnostics.
- ReliableMessages: add more transport packages, stronger lease/concurrency hardening, and provider-specific SQL Server/PostgreSQL polish.
- ContractEvolution: expand analyzer coverage and add richer graph/report export formats.

## Versioning

- Version packages independently.
- Keep namespaces under the `ServiceFoundry.*` prefix only for branding, not for shared architectural coupling.
- Avoid a shared platform package until duplication is proven.