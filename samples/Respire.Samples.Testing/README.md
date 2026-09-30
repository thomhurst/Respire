# Testing the same client code with fake and real servers

This runnable TUnit project uses only public APIs from `Respire`, `Respire.Testing`, and
`Respire.Testing.Containers`. The repository uses project references so the sample tests the
packages being built. In another solution, use NuGet references to the same released version
of both testing packages and a compatible TUnit version.

Install the .NET SDK selected by the repository's `global.json`. Run from the repository root:

```sh
# No Docker required. Eight fake cases, including RESP2 and RESP3.
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/FakeTests/*'

# Local Docker with Linux containers required. Four Redis/Valkey protocol cases.
dotnet run --project samples/Respire.Samples.Testing -c Release -f net10.0 -- --treenode-filter '/*/*/ContainerTests/*'
```

Use `-f net8.0` for .NET 8. Omit the filter to run all 12 cases, which requires Docker.
The CI workflow runs each suite on both frameworks. Container startup failures fail the tests;
they are not skipped when Docker is missing. First startup can include an image pull.

`SharedScenarios.cs` accepts `RespireOptions` and exercises the same binary value, hash/batch,
WATCH-abort, and acknowledged pub/sub scenarios against either server. The tests do not mock
client facets or reach into transport internals. Every case owns its server, and clients and
subscriptions are disposed before that server.

`FakeTests.cs` also demonstrates:

- Expiry exactly at its deadline using `RespireFakeClock`, without sleeping.
- A gate that holds an already accepted write's reply while another client observes its value.
  The fault's `Matched` task is the synchronization point, and `finally` releases and joins the write.
- A scoped `LOADING` rejection that does not mutate data; removing the scope restores normal writes.

`ContainerTests.cs` uses the public fixture with Redis 7.2 and Valkey 8.1 images. These family
tags can move; use a tested tag or digest when your application needs immutable image selection.
Each case starts its own standalone fixture with a two-minute startup deadline. The fixture
removes only its owned container. No external Redis instance, fixed port, or shared volume is used.

The fake supports a documented command subset in database zero. It does not reproduce
authentication, TLS, Cluster/Sentinel operation, persistence, eviction, modules, or server
performance. Unsupported commands fail explicitly. Its expiry clock does not control wall-clock
network deadlines. Use real fixtures for version compatibility and operational behavior; see the
[in-memory guide](../../website/docs/guides/in-memory-testing.md) and
[container guide](../../website/docs/guides/testing-containers.md).
