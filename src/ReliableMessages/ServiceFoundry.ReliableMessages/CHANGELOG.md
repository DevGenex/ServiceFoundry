# Changelog — ServiceFoundry.ReliableMessages

All notable changes to this package will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-05-21

### Added
- `MessagePublisher` — durable at-least-once outbox publish with correlation, causation, and trace propagation headers.
- `OutboxDispatcher` — background dispatcher with configurable batch size, lock duration, and retry backoff.
- `InboxProcessor` — inbox deduplication with at-most-once handler execution and poison-message dead-lettering.
- `ReliableMessagesMaintenance` — cleanup hooks for completed and dead-lettered outbox/inbox records.
- `IMessageTransport` abstraction — in-process transport included; pluggable for RabbitMQ and others.
- `AddReliableMessages` DI registration with builder pattern.
- OpenTelemetry-compatible `ActivitySource` and `Meter` instrumentation.
