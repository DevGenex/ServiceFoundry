# Changelog — ServiceFoundry.ConfigGuard

All notable changes to this package will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-05-21

### Added
- `ConfigContract<TOptions>` with required-key enforcement, unknown-key rejection, field aliases, alias deprecation warnings, and cross-field validation rules.
- `ConfigContractBuilder<TOptions>` DSL for constructing contracts with a fluent API.
- Wildcard-aware unknown-key detection for nested collection items (e.g. `Section:*:Property`).
- `ConfigValidationResult<TOptions>` and `ConfigDiagnostic` diagnostics model.
- `ConfigValidationException` thrown on fail-fast validation.
