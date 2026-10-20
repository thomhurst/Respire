---
title: Shared fake and container tests
description: Run the same public-client scenarios against an in-memory fake, Redis, and Valkey.
---

# Shared fake and container tests

The [testing sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Testing)
is a TUnit project that runs the same scenarios against the in-memory fake, Redis, and Valkey, on RESP2 and RESP3.
Use it as a template for sharing tests between fast fake runs and real-server runs.

The shared scenarios (binary values, hash/batch, WATCH abort, pub/sub) take a `RespireOptions`
parameter, so one function serves every server.

## Run it

Fake tests (8 cases, no Docker), from a repository checkout:

```sh
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/FakeTests/*'
```

These include three fake-only tests: advancing the expiry clock, pausing a write before its reply,
and removing a scoped server rejection. Gates and fault boundaries synchronize them without sleeps.

Container tests (4 cases: Redis and Valkey on RESP2 and RESP3) need a local Docker engine with Linux containers:

```sh
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/ContainerTests/*'
```

- Use `-f net8.0` for .NET 8.
- Omit the filter to run all cases, including the StackExchange.Redis examples below (requires Docker).
- Container startup errors fail the run; they are not skipped.

## StackExchange.Redis fixture replacement

`StackExchangeTests.cs` adds a shared suite whose assertions use only
`IConnectionMultiplexer` and `IDatabase`: strings, counters, hashes, lists and key deletion.
`StackExchangeFakeTests` supplies `RespireFakeConnection.Connection`.
`StackExchangeContainerTests` supplies a real StackExchange.Redis `ConnectionMultiplexer`
connected to Redis or Valkey. The scenario body is identical; only connection setup changes.

Run the compatibility fake sample without Docker or a TCP listener:

```sh
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/StackExchangeFakeTests/*'
```

Use the filter `/*/*/StackExchangeContainerTests/*` to run its two real-server counterparts.
In your own project, reference `Respire.Testing.StackExchangeCompat` and opt in to
[RSP0006](../diagnostics/RSP0006.md). Check the
[supported intersection and ownership rules](in-memory-testing.md#stackexchangeredis-test-fixtures)
before replacing your fixture. Script-heavy or topology-dependent suites still need real Redis.

## Notes

- Each case owns its server and clients, and disposes clients before the fixture.
- The sample uses `redis:7.2-alpine` and `valkey/valkey:8.1-alpine` with a two-minute startup deadline. These tags can move; pin a tested tag or digest for compatibility runs.
- The sample uses project references. In your own solution, reference matching released versions of `Respire.Testing`, `Respire.Testing.Containers`, and TUnit.

Use the fake for deterministic application behavior within its command subset. Use real servers for
authentication, TLS, topology and failover, persistence, eviction, modules, server-version differences,
and performance. The fake's clock controls expiry only, and `MOVED` injection does not simulate a Cluster.
See [fake limitations](in-memory-testing.md) and [container fixtures](testing-containers.md).
