---
title: Status and roadmap
description: What Respire supports today and what remains before a stable release.
---

# Status and roadmap

Respire is pre-release. Its core RESP2 client, typed command surface, pipelining, blocking command routing, pub/sub, streams, transactions, caching, dependency injection, and telemetry are implemented. Public APIs may still change.

## Available now

- Redis-style URI and `RespireOptions` connections
- Multiplexed connection pool with automatic pipelining
- String, key, hash, list, set, sorted-set, stream, bitmap, HyperLogLog, geo, script, and server facets
- Generated descriptors for every audited Redis, Valkey, module, KeyDB, and Dragonfly command
- Blocking list and stream commands on dedicated pooled connections
- Batches, transactions, and optimistic concurrency with `WATCH`
- Pub/sub, pattern subscriptions, and Redis 7 sharded pub/sub
- Typed JSON serialization and custom `IRespireSerializer`
- Raw and interpolated command execution
- Automatic reconnect and pub/sub resubscription
- TLS connections through `rediss://` or `RespireOptions.UseTls`
- Dependency injection, distributed caching, `HybridCache`, and OpenTelemetry

## Not implemented yet

| Capability | Current behavior |
| --- | --- |
| Redis Cluster | Single endpoint only |
| Redis Sentinel | Not supported |
| RESP3-first internals | Protocol option exists; broader adoption remains planned |
| Client-side caching | Tracking and invalidation not shipped |

If one of these is a hard requirement today, use a mature client such as StackExchange.Redis.

## Design source

The full surface, tradeoffs, wire architecture, and future work live in the repository's [API design specification](https://github.com/thomhurst/Respire/blob/main/docs/API_DESIGN.md). The longer [Why Respire](https://github.com/thomhurst/Respire/blob/main/docs/WHY_RESPIRE.md) document explains the product bets and where the client fits.

Track changes and contribute through [GitHub issues](https://github.com/thomhurst/Respire/issues).
