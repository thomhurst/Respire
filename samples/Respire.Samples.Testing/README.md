# Testing the same client code with fake and real servers

A TUnit project that runs the same scenarios against the in-memory fake server
(`Respire.Testing`) and real Redis/Valkey containers (`Respire.Testing.Containers`),
using only public APIs. In your own solution, reference the same released version
of both testing packages and a compatible TUnit.

You need the .NET SDK from `global.json`. Run from the repository root:

```sh
# No Docker required. Eight fake cases, including RESP2 and RESP3.
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/FakeTests/*'

# Local Docker with Linux containers required. Four Redis/Valkey protocol cases.
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/ContainerTests/*'
```

Use `-f net8.0` for .NET 8. Without a filter, all cases run and Docker is
required. Missing Docker fails the container tests; they are not skipped.

- `SharedScenarios.cs` takes `RespireOptions` and runs binary values,
  hash/batch, `WATCH` abort, and pub/sub against either server. Each case owns
  its server and disposes clients first.
- `FakeTests.cs` also shows:
  - Expiry exactly at the deadline with `RespireFakeClock`, without sleeping.
  - Holding a write's reply while another client reads the value, synchronized
    on the fault's `Matched` task.
  - A scoped `LOADING` rejection that leaves data unchanged.
- `ContainerTests.cs` uses Redis 7.2 and Valkey 8.1 tags, which can move; pin a
  tag or digest if you need a fixed image. Each case starts its own container
  (two-minute startup limit) and removes only that container.

The fake supports a documented command subset in database zero. It has no
authentication, TLS, Cluster, Sentinel, persistence, eviction, modules, or
realistic performance, and unsupported commands fail. Its clock does not control
network timeouts. Use real containers for version and operational behavior. See
the [in-memory guide](../../website/docs/guides/in-memory-testing.md) and
[container guide](../../website/docs/guides/testing-containers.md).
