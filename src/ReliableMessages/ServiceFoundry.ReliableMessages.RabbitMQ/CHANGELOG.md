# Changelog — ServiceFoundry.ReliableMessages.RabbitMQ

All notable changes to this package will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-05-21

### Added
- `RabbitMqTransportOptions` — connection URI, exchange name/type, persistent delivery, and custom routing key resolver.
- `UseRabbitMqTransport` builder extension for registering the RabbitMQ `IMessageTransport`.
- Persistent topic exchange publishing with full service message header propagation (trace, causation, correlation).
- Automatic reconnection on dropped `IConnection` or closed `IModel` with thread-safe lazy channel creation.
- Graceful `IDisposable` teardown of connection and channel resources.
