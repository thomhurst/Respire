---
title: Generated custom commands
---

Declare command interfaces for custom Redis modules without reflection or runtime code generation. The `Respire` NuGet package includes the generator. It creates a sealed class named `<InterfaceName>Implementation` in the interface's namespace; construct it with an `IRespireClient`.

<!-- doc-test-ignore: Generator interfaces must be top-level; the documentation harness nests declarations. The packed-package Native AOT smoke project validates the same construction and command shapes. -->
```csharp
using Respire;

[RespireCommands]
public interface IJsonCommands
{
    [RespireCommand("JSON.GET")]
    ValueTask<string?> GetAsync(
        RespireKey key,
        string path = "$",
        CancellationToken cancellationToken = default);

    [RespireCommand("JSON.MGET")]
    ValueTask<string?[]> GetManyAsync(RespireKey[] keys, string path = "$");

    [RespireCommand("JSON.DEBUG")]
    ValueTask<RespireResult> DebugAsync(string subcommand, RespireKey key);
}
```

Given a connected `IRespireClient client`, construct `new IJsonCommandsImplementation(client)` and call `GetAsync("document")`. The server must provide these module commands. The generator does not install modules or validate server versions.

## Declarations and arguments

Interfaces must be public or internal, top-level, non-generic, and have no base interfaces. Partial interfaces and overloaded methods are supported. Members must be public abstract instance methods with `[RespireCommand]`. Unsupported declarations produce the `RESP003` compilation error, whose message names the violated rule and whose help link points to this guide. The generated class name must be available in the namespace.

A command attribute contains one printable ASCII token, such as `JSON.GET`; it is normalized to uppercase and encoded once in a static descriptor. Pass subcommands and options as ordinary method arguments. Parameters are sent in declaration order. A single `CancellationToken` and a single `RespireCommandFlags` parameter are recognized as execution controls and are not sent to Redis.

Supported scalar arguments are `RespireKey`, `RespireValue`, strings, byte arrays, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ArraySegment<byte>`, `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `char`, `Guid`, `DateTimeOffset`, and `TimeSpan`. Each uses the existing `RespireValue` conversion. One-dimensional arrays of supported scalars expand into separate arguments; `byte[]` is always one binary argument. `params` arrays and optional constant/default parameters are supported. Empty expanded arrays contribute no arguments; null expanded arrays throw. Optional arguments are always sent with their chosen value, never silently omitted. Use an explicit `RespireValue[]` for variable option lists.

## Results and ownership

Methods return `Task` or `ValueTask`, optionally with a result type:

- `string`, `byte[]`, `bool`, `int`, `long`, or `double`, their nullable forms, and one-dimensional arrays of these scalar types.
- `RespireResult` for arbitrary module responses, including RESP2/RESP3 shape differences and nested aggregates.

Nullable results preserve RESP nulls. A null reply for a non-nullable result throws `InvalidOperationException`. Reference types declared in a nullable-oblivious context (`#nullable disable`) are treated as nullable, so `Task<string>` there returns `null` for a RESP null. `int` results use a checked conversion. Booleans accept RESP booleans or integer flags, and doubles accept RESP doubles or invariant-culture textual numbers. Aggregate scalar conversion follows the server's element order; map keys and values are flattened as exposed by `RespireResult`. It does not infer a module-specific object model.

Typed methods copy strings/bytes as needed and dispose the pooled root reply, including when conversion fails. Methods without a result still await server success and dispose the reply. Methods returning `RespireResult` transfer its lease to the caller: use `using var result = await commands.DebugAsync("MEMORY", "document")`, and do not retain nested views beyond that lease. User-defined DTOs and arbitrary serializers are not inferred; obtain raw bytes/string and explicitly apply source-generated serialization metadata where needed.

## Execution policies

Generated methods use `IRespireClient.ExecuteAsync` with `RespireCommand.Create`. This preserves catalog command safety, cancellation, routing, redirection flags, and client-side cache invalidation. An already-canceled token is rejected before argument encoding or sending; cancellation after admission does not guarantee the server did not execute the command. Connection-affine operations such as `AUTH` remain rejected. Unknown module commands conservatively invalidate the local cache; a generated declaration does not make them cacheable or read-only. Declaring a module command read-only is not supported yet, so read-only module calls such as `JSON.GET` also invalidate client-side cache entries.

Argument, cancellation, and safety failures are reported through the returned task for every return shape, including the non-async `ValueTask<RespireResult>` shape; a call does not throw before returning its task.

Unknown module commands are treated as non-blocking and run on the shared multiplexed connections. Do not declare module commands that wait server-side for data (for example, blocking pops); they would stall every command queued behind them on that connection.

Key-prefixed views reject generated commands, just as they reject raw/catalog commands, because arbitrary module key layouts are unknown. The exception is a command whose key layout Respire registers as prefixable, such as the first-party TimeSeries and probabilistic commands; their keys are prefixed. A `RespireKey` parameter alone does not declare routing metadata. Known cluster layouts retain slot validation; unknown layouts use existing raw routing heuristics and server validation. Use unprefixed clients and explicitly formed keys, and verify your module's routing requirements. The first-party JSON, Search, TimeSeries, and probabilistic packages remain separate work under [#417](https://github.com/thomhurst/Respire/issues/417).

For direct repeated raw execution, retain a static `RespireCommand.Create("MYMODULE.COMMAND")` descriptor. Its `Sources` remains `None`; pre-encoding does not claim an official command reference. String-to-command conversion retains its existing parsing behavior.

## Native AOT

Generated code calls concrete APIs and does not use reflection, `RespireResult.As<T>()`, or runtime code generation. CI packs `Respire`, consumes its analyzer assets in `tests/Respire.GeneratedCommands.Smoke`, publishes that consumer with Native AOT warnings treated as errors, and executes both RESP2 and RESP3 command paths against Redis. Project-reference consumers inside a repository must also reference `Respire.Analyzers` with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`; NuGet consumers receive the generator automatically.
