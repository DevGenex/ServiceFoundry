# Changelog — ServiceFoundry.ReliableMessages.EFCore

All notable changes to this package will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-05-21

### Added
- `EntityFrameworkReliableMessagesStore<TDbContext>` — EF Core-backed `IOutboxStore` and `IInboxStore`.
- `SaveChangesAndEnqueueAsync` — atomically commits the current `DbContext` transaction and enqueues pending outbox messages in a single operation.
- Entity type configurations for `OutboxMessage` and `InboxMessage`.
- `UseEntityFramework` builder extension for registering the EF Core store.
