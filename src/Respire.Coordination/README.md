# Respire.Coordination

Distributed coordination primitives including hosted leader election, fenced locks, rate limiters, semaphores, latches, and leases.

## Install

```sh
dotnet add package Respire.Coordination
```

## Usage

```csharp
using Respire;
using Respire.Coordination;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var coordination = new RespireCoordination(redis);
await using var attempt = await coordination.TryAcquireFencedLockAsync(
    "{invoice:42}:lease", "{invoice:42}:fence", TimeSpan.FromSeconds(30));
if (attempt.Acquired)
    Console.WriteLine(attempt.Lock.FencingToken);
```

Pass the fencing token with protected writes; the receiving resource must enforce it. The guide covers failure and lease-expiry semantics.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/coordination) for configuration, requirements, and further examples.
