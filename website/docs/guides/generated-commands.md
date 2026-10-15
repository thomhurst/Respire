---
title: Generated custom commands
---

Declare an interface for custom Redis module commands, and a source generator implements it without reflection or runtime code generation. The generator ships in the `Respire` NuGet package. It creates a sealed `<Name>Implementation` class in the interface's namespace; construct it with an `IRespireClient`. A conventional interface prefix (`I` followed by an uppercase letter) is removed: `IJsonCommands` produces `JsonCommandsImplementation`. Other names are kept, so `Inventory` produces `InventoryImplementation`.

<!-- doc-test-ignore: Generator interfaces must be top-level; the documentation harness nests declarations. The packed-package Native AOT smoke project validates the same construction and command shapes. -->
```csharp
using Respire;

[RespireCommands]
public interface IJsonCommands
{
    [RespireCommand("JSON.GET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<string?> GetAsync(
        RespireKey key,
        string path = "$",
        CancellationToken cancellationToken = default);

    [RespireCommand("JSON.MGET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<string?[]> GetManyAsync(RespireKey[] keys, string path = "$");

    [RespireCommand("JSON.DEBUG")]
    ValueTask<RespireResult> DebugAsync(string subcommand, RespireKey key);
}
```

Then call `new JsonCommandsImplementation(client).GetAsync("document")`. The server must provide the module commands; the generator does not install modules or check server versions.

## Declarations and arguments

Interface rules:

- Public or internal, top-level, non-generic, with no base interfaces. Partial interfaces and overloads are supported.
- Members are public abstract instance methods marked `[RespireCommand]`.
- The generated class name must be free in the namespace.
- Violations produce compile error `RESP003`, which names the rule and links to this guide.

Command names:

- Normally one printable ASCII token, such as `JSON.GET`. It is uppercased and encoded once.
- A multi-token name works only if it has a dedicated `RespireCommands` descriptor field, such as `FT.CURSOR READ`. A catalog entry alone is not enough: `FT.CURSOR GC` has no dedicated field and produces `RESP003`.
- Multi-token names use single spaces, and their cache mutation policy cannot be overridden.
- Pass other subcommands and options as method arguments.

Parameters are sent in declaration order. One `CancellationToken` and one `RespireCommandFlags` parameter are execution controls and are not sent.

Supported scalar arguments: `RespireKey`, `RespireValue`, strings, byte arrays, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ArraySegment<byte>`, `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `char`, `Guid`, `DateTimeOffset`, and `TimeSpan`. Each uses the `RespireValue` conversion.

- One-dimensional arrays of these expand into separate arguments; `byte[]` is always one binary argument.
- `params` arrays and optional parameters with defaults are supported. Optional arguments are always sent, never omitted.
- Empty arrays add no arguments; null arrays throw.
- Use an explicit `RespireValue[]` for variable option lists.

## Results and ownership

Methods return `Task` or `ValueTask`, optionally with a result:

- `string`, `byte[]`, `bool`, `int`, `long`, or `double`, their nullable forms, and one-dimensional arrays of them.
- `RespireResult` for arbitrary replies, including RESP2/RESP3 differences and nested aggregates.

Conversion rules:

- Nullable results keep RESP nulls. A null reply for a non-nullable result throws `InvalidOperationException`.
- Under `#nullable disable`, reference types count as nullable, so `Task<string>` returns `null` for a RESP null.
- `int` uses a checked conversion. Booleans accept RESP booleans or integer flags. Doubles accept RESP doubles or invariant-culture text.
- Aggregates convert in server element order, with maps flattened as `RespireResult` exposes them. No module-specific object model is inferred.

Typed methods copy what they need and dispose the pooled reply, even when conversion fails. Methods without a result still await server success.

A returned `RespireResult` is a lease you own. Use `using var result = await commands.DebugAsync("MEMORY", "document")`, and do not keep nested views past the lease. DTOs and serializers are not inferred: read raw bytes or strings, then apply source-generated serialization yourself.

## Execution policies

Generated methods call `IRespireClient.ExecuteAsync` with a static descriptor, so command safety checks, cancellation, routing, redirect flags, and client-side cache invalidation all apply.

- An already-canceled token is rejected before encoding or sending. Cancellation after admission does not prove the server skipped the command.
- Connection-affine commands such as `AUTH` stay rejected.
- Single-token generated commands have retry category `Never`, so `RespireOptions.RetryPolicy` never resends them.
- All failures (arguments, cancellation, safety) surface through the returned task, for every return shape including non-async `ValueTask<RespireResult>`. The call never throws before returning.

### Cache mutation

Set `Mutation = RespireCacheMutation.ReadOnly` only when a command does not change keyspace values, as in the JSON reads above. It declares the cache effect only: it does not make the command cacheable, register its key layout, or allow replica reads. Without it, unknown commands invalidate the whole local cache. For writes, choose `SingleKey`, `MultiKey`, or `Mutation` to match the key-layout contract. The generator does not infer affected keys from parameters.

:::warning
Unknown module commands run on the shared multiplexed connections. Do not declare commands that wait server-side, such as blocking pops: they stall every command queued behind them.
:::

### Key prefixes and routing

- Prefixed views reject generated commands unless their layout is registered as prefixable. Prefixable commands, such as `TS.GET` and `BF.ADD`, get their keys prefixed.
- `TS.MGET`, `TS.MRANGE`, `TS.MREVRANGE`, `TS.QUERYINDEX`, and `TS.QUERYLABELS` have no explicit keys, so prefixed views reject them.
- A `RespireKey` parameter does not declare routing. Known Cluster layouts keep slot validation; unknown layouts use raw routing heuristics and server validation.
- For unknown layouts, use an unprefixed client with fully formed keys, and check your module's routing needs.

## Built-in Redis feature packages

The same generator backs the typed feature packages, which add feature-specific options and reply models:

- [Respire.Json](json.md): RedisJSON commands and source-generated JSON serialization.
- [Respire.Search](search.md): query builders, aggregation and cursors, vector queries, and hybrid search.
- [Respire.TimeSeries](timeseries.md): samples, ranges, aggregation, and retention rules.
- [Respire.Probabilistic](probabilistic.md): Bloom, Cuckoo, Count-Min, Top-K, and t-digest.

Each supports RESP2 and RESP3. The server must provide the commands; hybrid search requires Redis 8.4 or a compatible server.

To run a module command without an interface, pass a static `RespireCommand.Create("MYMODULE.COMMAND")` descriptor to `ExecuteAsync`. Its `Sources` is `None`, since no official reference lists it. See the [complete command catalog](raw-commands.md#dynamic-commands).

## Native AOT

Generated code calls concrete APIs and uses no reflection, `RespireResult.As<T>()`, or runtime code generation, so it is Native AOT compatible. A packed-package consumer is published with AOT warnings as errors and run against Redis 8.10.

NuGet consumers get the generator automatically. A project referencing the Respire source projects directly must also reference `Respire.Analyzers` with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`.
