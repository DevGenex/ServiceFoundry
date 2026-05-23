# Changelog — ServiceFoundry.ContractEvolution

All notable changes to this package will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-05-21

### Added
- `ContractEvolutionBuilder` DSL — `ForContract<T>`, `Version<T>`, `Latest<T>`, and `Map<TFrom,TTo>`.
- `ContractMapBuilder<TFrom,TTo>` — `Copy`, `Rename`, `Default`, and `Compute` binding types.
- `ContractEvolutionEngine` — compiled upgrade graph with BFS multi-hop path resolution.
- `IContractEvolutionEngine` — public interface for runtime upgrade, plan resolution, and compatibility assessment.
- `ContractUpgradePlan` and `CompatibilityAssessment` result types.
- `IContractEvolutionReportProvider` and `GetReport`/`GetReports` surface on the engine.
- `AddContractEvolution` DI registration with `IContractEvolutionEngine` and `IContractEvolutionReportProvider` singletons.
