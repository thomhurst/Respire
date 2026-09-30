---
title: Shared fake and container tests
description: Run the same public-client scenarios against an in-memory fake, Redis, and Valkey.
---

# Shared fake and container tests

The [runnable testing sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Testing)
is a TUnit project using both testing packages through their public APIs. It shares binary
value, hash/batch, WATCH-abort, and pub/sub scenarios through one function accepting
`RespireOptions`. Each scenario runs with RESP2 and RESP3 on the fake, Redis, and Valkey.

From a repository checkout with its required .NET SDK installed:

```sh
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/FakeTests/*'
```

This runs eight cases without Docker. Additional fake tests advance an expiry clock, pause
an accepted write before its reply, and remove a scoped server rejection. Gates and observed
fault boundaries synchronize the tests; arbitrary sleeps are unnecessary.

For four real-server cases, start a local Docker engine with Linux containers and run:

```sh
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/ContainerTests/*'
```

Use `-f net8.0` for .NET 8. Omit the filter to run all 12 cases, requiring Docker.
The sample workflow runs both suites on both frameworks and retains test reports.
Container startup errors fail the run rather than silently skipping integration coverage.

Each case owns its server and clients. Clients and subscriptions are disposed first; fixture
disposal removes only that case's container. The sample selects `redis:7.2-alpine` and
`valkey/valkey:8.1-alpine` with a two-minute startup deadline. These family tags can move;
select a tested tag or digest for immutable application compatibility runs.

Repository project references exercise the current package source. When copying the sample
into another solution, reference matching released versions of `Respire.Testing` and
`Respire.Testing.Containers`, plus TUnit. The sample has no access to Respire internals.

Use the fake for deterministic application behavior within its supported command subset.
Use real servers for authentication, TLS, topology/failover, persistence, eviction, modules,
server-version differences, and performance. The fake's clock controls expiry only, and
MOVED injection does not simulate a Cluster. See [fake limitations](in-memory-testing.md)
and [container lifecycle and networking](testing-containers.md).
