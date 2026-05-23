# Changelog — ServiceFoundry.ContractEvolution.Analyzers

All notable changes to this package will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-05-21

### Added
- **SFCE001** `MissingTargetBindingAnalyzer` — reports a warning when a `Map<TFrom,TTo>` call leaves one or more target properties unbound (not covered by an explicit binding, `Default`, or a same-named source property copy).
