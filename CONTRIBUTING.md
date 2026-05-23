# Contributing to ServiceFoundry

Thank you for your interest in contributing. This document covers how to build, test, and submit changes.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Any terminal (PowerShell, bash, or cmd)
- Git

## Building

From the repository root:

```powershell
dotnet build ServiceFoundry.slnx
```

## Running all tests

```powershell
dotnet test ServiceFoundry.slnx
```

To run tests for a single product area:

```powershell
dotnet test tests/ConfigGuard/ServiceFoundry.ConfigGuard.Tests/ServiceFoundry.ConfigGuard.Tests.csproj
dotnet test tests/ReliableMessages/ServiceFoundry.ReliableMessages.Tests/ServiceFoundry.ReliableMessages.Tests.csproj
dotnet test tests/ContractEvolution/ServiceFoundry.ContractEvolution.Tests/ServiceFoundry.ContractEvolution.Tests.csproj
```

## Project layout

```
src/
  ConfigGuard/            # ConfigGuard packages
  ReliableMessages/       # ReliableMessages packages
  ContractEvolution/      # ContractEvolution packages
tests/                    # One test project per source package
samples/                  # Usage examples
docs/                     # Roadmap and design notes
```

## Coding conventions

- All public types and members must have XML documentation comments.
- Nullable reference types are enabled everywhere — no `!` suppressions without a comment.
- No internal state on static types.
- Tests use xUnit. Follow the `Method_does_expected_thing_when_condition` naming pattern.
- Prefer `sealed` for concrete types unless inheritance is by design.

## Submitting a pull request

1. Fork the repository and create a feature branch off `main`.
2. Make your change with a focused scope — one fix or feature per PR.
3. Ensure `dotnet test ServiceFoundry.slnx` passes with no new failures.
4. Open a PR against `main` with a clear description of what changed and why.
5. All CI checks must be green before merge.

## Reporting issues

Open a GitHub issue. For security issues see [SECURITY.md](SECURITY.md).
