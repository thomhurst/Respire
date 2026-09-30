---
title: Status and roadmap
description: What Respire supports today and what remains before a stable release.
---

# Status and roadmap

Respire is pre-release. Its RESP3-preferred client with bounded RESP2 fallback, typed command surface, pipelining, blocking command routing, pub/sub, streams, transactions, caching, dependency injection, and telemetry are implemented. Public APIs may still change.

## Available now

- Redis-style URI and `RespireOptions` connections
- Automatic RESP3 preference with unsupported-HELLO fallback, strict protocol overrides, and typed RESP2/RESP3 reply normalization
- Multiplexed connection pool with automatic pipelining
- String, key, hash, list, set, sorted-set, stream, bitmap, HyperLogLog, geo, script, and server facets
- Generated descriptors for every audited Redis, Valkey, module, KeyDB, and Dragonfly command
- Blocking list and stream commands on dedicated pooled connections
- Batches, transactions, and optimistic concurrency with `WATCH`
- Pub/sub, pattern subscriptions, and Redis 7 sharded pub/sub
- [Typed standalone keyspace, keyevent, and Redis 8.8 subkey notifications](guides/keyspace-notifications.md)
- Lazy/eager Redis Sentinel discovery and reactive primary handoff after disconnect or READONLY
- Typed JSON serialization and custom `IRespireSerializer`
- Raw and interpolated command execution
- Automatic reconnect and pub/sub resubscription
- TLS connections through `rediss://` or `RespireOptions.UseTls`
- Bounded RESP3 server-assisted client-side caching for eligible Redis reads, with OPTIN or BCAST/prefix tracking
- Dependency injection, distributed caching, `HybridCache`, and OpenTelemetry
- An in-memory testing server with controlled expiry/faults, Redis/Valkey container fixtures,
  and a [shared test sample](guides/testing-sample.md) for both supported frameworks

## Not implemented yet

| Capability | Current behavior |
| --- | --- |
| Redis Cluster gaps | Cluster routing and same-slot `WATCH` transactions are supported; sharded pub/sub and typed notification fan-out remain unavailable in cluster mode |
| Sentinel event monitoring | Lazy discovery and reactive re-discovery are supported; Sentinel event subscriptions and the real-server failover matrix remain planned |

If one of these is a hard requirement today, use a mature client such as StackExchange.Redis.

## Design source

The full surface, tradeoffs, wire architecture, and future work live in the repository's [API design specification](https://github.com/thomhurst/Respire/blob/main/docs/API_DESIGN.md). The longer [Why Respire](https://github.com/thomhurst/Respire/blob/main/docs/WHY_RESPIRE.md) document explains the product bets and where the client fits.

Track changes and contribute through [GitHub issues](https://github.com/thomhurst/Respire/issues).
