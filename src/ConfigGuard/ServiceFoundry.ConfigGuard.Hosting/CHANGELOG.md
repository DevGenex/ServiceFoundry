# Changelog — ServiceFoundry.ConfigGuard.Hosting

All notable changes to this package will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-05-21

### Added
- `AddConfigContract<TOptions>` extension on `IServiceCollection` — registers a contract with `IValidateOptions<T>` and optional fail-fast hosted service.
- `AddNamedConfigContract<TOptions>` — named-options variant resolving through `IOptionsMonitor<T>.Get(name)`.
- `FailFastOnStartup()` builder method — adds a hosted service that throws on host start if validation fails.
