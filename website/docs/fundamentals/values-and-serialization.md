---
title: Values and serialization
description: Understand RespireKey, RespireValue, typed objects, and leased reads.
---

# Values and serialization

Commands take flexible `RespireKey` and `RespireValue` inputs. Reads return the representation you
choose at the call site: text, bytes, a typed object, pooled memory, or a stream.

## Keys and input values

`RespireKey` accepts `string`, `byte[]`, or `ReadOnlyMemory<byte>`. `RespireValue` converts implicitly from keys, text, binary data (`byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, `ArraySegment<byte>`), numeric primitives, booleans, `Guid`, `DateTimeOffset`, `TimeSpan`, and `char`.

```csharp
RespireKey key = "counter";
RespireValue value = 42;

await redis.SetAsync(key, value);
```

Both use value equality. `RespireValue` compares the exact bulk-string payload sent to Redis, so `5`, `"5"`, and the UTF-8 bytes for `5` are equal and share a hash code.

| Type | Encoding |
| --- | --- |
| `Guid` | Invariant `D` format |
| `DateTimeOffset` | Invariant round-trip `O` format |
| `TimeSpan` | Invariant constant `c` format |
| `char` | One-character text |

## Span formatting and parsing

Both types implement `ISpanFormattable`, `IUtf8SpanFormattable`, and `ISpanParsable<T>`.
Interpolating existing keys or values into a character span or UTF-8 destination does not
allocate in optimized steady state, including binary and prefixed keys:

```csharp
using System.Text.Unicode;
using Respire;

RespireKey key = "counter";
RespireValue value = 42;
Span<char> text = stackalloc char[64];
Span<byte> utf8 = stackalloc byte[64];
bool textFits = text.TryWrite($"{key}={value}", out int charsWritten);
bool utf8Fits = Utf8.TryWrite(utf8, $"{key}={value}", out int bytesWritten);
```

Direct `TryFormat` calls return `false` and a written count of zero if the destination is
too small; its contents may have changed. The default format and `G`/`g` produce the same
text as `ToString()`. Other formats throw `FormatException`. Format providers are ignored:
numbers retain invariant Redis formatting, booleans use `1`/`0`, and `RespireValue.Null`
formats as empty text. `string.Create(provider, $"{key}={value}")` also uses span formatting,
but still allocates the resulting string.

Formatting is for text and logging, not binary serialization. Binary inputs decode as UTF-8
with replacement characters for invalid sequences. UTF-8 formatting encodes that text;
commands continue to send the original binary bytes unchanged.

`Parse` and `TryParse` accept all text verbatim, including whitespace and empty strings;
they do not infer numeric types or a null sentinel. Span parsing copies the text into an
owned string. String parsing reuses its immutable input. A null string throws
`ArgumentNullException` from `Parse`; `TryParse` returns `false` with `RespireKey.Empty`
or `RespireValue.Null`.

Collection arguments already accept collection expressions through arrays and
`ReadOnlySpan<T>`. `RespireResult` is a disposable view of response buffers and has no
collection builder. Numeric increment overloads retain `long` and `double` because they
select different Redis commands and result types; bitfield operands remain `long` with
explicit field width and overflow options. A general `INumber<T>` overload would admit
types Redis cannot represent without narrowing or a separate conversion policy.

## Typed output

```csharp
string? text = await redis.GetStringAsync("payload");
byte[]? bytes = await redis.GetBytesAsync("payload");
Order? order = await redis.GetAsync<Order>("order:42");
```

A missing key returns `null` from string, byte-array, reference-type, and nullable reads. For a non-nullable value type, `GetAsync<T>` returns `default(T)`, so a missing `GetAsync<int>` is `0`. Numeric and condition commands return `long`, `double`, or `bool`.

## Missing keys versus stored defaults

`TryGetAsync<T>` returns a `RespireGetResult<T>` that reports presence alongside the value, so you can tell a missing key from a stored `default(T)` in one round trip:

```csharp
var (found, hits) = await redis.TryGetAsync<int>("page:hits");

if (!found) { /* key is absent */ }
else if (hits == 0) { /* key holds 0 */ }
```

`RespireGetResult<T>` also has `GetValueOrDefault(fallback)`. The same method exists on `redis.Strings` and, per field, on `redis.Hashes`.

## Object serialization

`SetAsync<T>` and `GetAsync<T>` use `RespireOptions.Serializer` for objects. The default is `RespireSystemTextJsonSerializer`. Supply an `IRespireSerializer` for another format:

<!-- doc-test-ignore: Optional serializer package; shown with application connection options. -->
```csharp
var options = new RespireOptions
{
    Endpoints = { new("localhost") },
    Serializer = new Respire.Serialization.MessagePack.RespireMessagePackSerializer(),
};
```

A custom serializer must implement all four `IRespireSerializer` methods: generic
`Serialize<T>` / `Deserialize<T>` and runtime-type `Serialize(..., Type, object?)` /
`Deserialize(Type, ...)`. See [NativeAOT and trimming](#nativeaot-and-trimming) for a forwarding example.

What bypasses the serializer:

- Typed `string`, `byte[]`, `ReadOnlyMemory<byte>`, `Memory<byte>`, `ArraySegment<byte>`, `char`, Boolean, and numeric primitives. Nullable scalar primitives with a value also bypass serialization. Custom serializers do not control their encoding.
- Byte-memory values store only the selected slice as raw bytes, including empty slices. This applies to root and facet writes, batches, transactions, and `GetOrSetAsync<T>`. Typed reads of these byte-memory types return a copy that remains valid after the response is released.
- Numbers use invariant Redis text. Booleans write `1`/`0` on every path (generic, raw, and collection members); reads also accept `true`/`false`.
- Objects, enums, and other types use the configured serializer. Pass a `RespireValue` when an input must use raw Redis scalar conventions.

:::warning Upgrading existing byte-memory values
This changes the stored format for `ReadOnlyMemory<byte>`, `Memory<byte>`, and `ArraySegment<byte>` values previously written through a serializer. Existing JSON/base64 data is not migrated: typed reads now return those stored bytes, not the original decoded payload. Rewrite existing keys from the original values, use new keys, or explicitly decode the old format with the serializer that wrote it before rewriting. This also applies to typed script/function results (`RespireResult.As<T>()`) and pub/sub messages (`RespireMessage.As<T>()`), which now read byte-memory values as raw bytes.
:::

Facets that take a single payload have a generic overload beside the `RespireValue` one: `Hashes.SetAsync<T>`, `Sets.ContainsAsync<T>`, `SortedSets.AddAsync<T>`, and the `Lists.LeftPopAsync<T>` / `RightPopAsync<T>` reads. An argument typed as `RespireValue` selects the raw overload; anything else selects the generic one. Generic byte-memory and `char` writes keep their `RespireValue` encoding. Collection-member overloads also preserve raw non-finite floating-point values; generic string writes reject them.

## Binary serializer packages

Install the adapter for your format; each package pins the matching core Respire version.
Both generic and runtime-Type methods implement the existing `IRespireSerializer` contract.

| Package | Serializer | Model setup | Adapter copies |
| --- | --- | --- | --- |
| `Respire.Serialization.MessagePack` | `RespireMessagePackSerializer` | `[MessagePackObject]` and `[Key]`, or custom resolvers | Direct writer; reads copy the span into one owned array |
| `Respire.Serialization.Protobuf` | `RespireProtobufSerializer` | protobuf-net `[ProtoContract]` / `[ProtoMember]`, or a supplied `TypeModel` | Direct writer and span; no adapter payload copy |
| `Respire.Serialization.MemoryPack` | `RespireMemoryPackSerializer` | `[MemoryPackable]` on a partial type; reference MemoryPack in the model project for its generator | Direct writer and span; no adapter payload copy |

MessagePack defaults to `Standard.WithSecurity(MessagePackSecurity.UntrustedData)`.
Its read copy is necessary because the upstream serializer accepts memory or a sequence,
not a span. The copy is owned rather than pooled so custom formatters can retain memory.
Protobuf has no root null representation: this adapter rejects null writes; an empty
protobuf message represents a default-valued object. MemoryPack and MessagePack support
null roots. Configure options and models before sharing a serializer between threads;
custom converters must also support concurrent calls.

Each package provides a DI helper in its matching namespace. Registration supplies a
singleton `IRespireSerializer`; explicitly select it in the client options factory:

<!-- doc-test-ignore: Requires the optional MessagePack serializer package and application DI setup. -->
```csharp
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.Serialization;
using Respire.Serialization.MessagePack;

services.AddRespireMessagePackSerializer();
services.AddRespire(provider => RespireOptions.Parse("localhost:6379") with
{
    Serializer = provider.GetRequiredService<IRespireSerializer>(),
});
```

Use `AddRespireProtobufSerializer(model)` or `AddRespireMemoryPackSerializer(options)`
in the same way. Registration does not reconfigure existing clients or automatically
choose serializers for keyed clients. Register one default serializer, or construct
separate instances in each keyed client's options factory.

Typed values and `GetOrSetAsync<T>` use the client serializer. Job queues and near-cache
maps take explicit byte delegates; connect the same adapter to those delegates:

<!-- doc-test-ignore: Optional adapter, application Order model, and existing client with ClientSideCache enabled. -->
```csharp
byte[] Encode(Order order)
{
    var buffer = new System.Buffers.ArrayBufferWriter<byte>();
    serializer.Serialize(buffer, order);
    return buffer.WrittenSpan.ToArray();
}
Order Decode(ReadOnlyMemory<byte> bytes) => serializer.Deserialize<Order>(bytes.Span)!;

var queue = new Respire.Streaming.RespireJobQueue<Order>(redis, "orders", Encode);
await using var map = new RespireMap<string, Order>(redis, "orders:map", key => key, Encode, Decode);
// Pass Encode and bytes => Decode(bytes) to AddRespireJobQueue when registering a worker.
```

Those delegate APIs require owned arrays, so encoding copies the written bytes regardless
of format. The typed-serialization benchmark workflow compares these adapters with
System.Text.Json on small and large models, including MessagePack's read copy in its
latency and allocation measurements.

**Trimming and AOT:** MemoryPack and MessagePack need generated/preserved formatters for
every model. Their reflection fallback and runtime-Type dispatch are not a general AOT
guarantee. Protobuf-net's default runtime model and this adapter's runtime-Type writer
use runtime metadata/generic dispatch; they are not advertised as NativeAOT-safe. All
adapters retain the interface's trimming and dynamic-code warnings. For Respire's
supported reflection-free path, use the generated JSON context below; validate any
binary configuration against your own published model graph.

See the upstream [MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp),
[MemoryPack](https://github.com/Cysharp/MemoryPack), and
[protobuf-net](https://github.com/protobuf-net/protobuf-net) model and compatibility guidance.
Formats are not interchangeable: migrate stored data or use a new key namespace when
switching serializers, and configure every producer and consumer consistently.

## NativeAOT and trimming

Typed values use reflection-based System.Text.Json metadata by default. For a trimmed or NativeAOT
app, generate metadata for every stored type and pass that context to Respire:

<!-- doc-test-tail-declaration: split-before=[JsonSerializable -->
```csharp
using System.Text.Json.Serialization;
using Respire;
using Respire.Serialization;

var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    Serializer = RespireSystemTextJsonSerializer.FromContext(AppJsonContext.Default),
};

await using var redis = await RespireClient.ConnectAsync(options);

// The generic APIs are conservatively annotated because IRespireSerializer can be
// reflection-based. This configured context makes these two calls AOT-safe.
#pragma warning disable IL2026, IL3050
await redis.SetAsync("user:1", new User("Ada", 36));
User? user = await redis.GetAsync<User>("user:1");
#pragma warning restore IL2026, IL3050

[JsonSerializable(typeof(User))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
```

Add a `[JsonSerializable]` entry for each non-primitive type. Strings, byte arrays, Booleans, and
numbers use built-in codecs and need no JSON metadata.

A custom serializer or decorator must forward the declared type in the `Type`-based methods, not
substitute `object` or `value.GetType()`:

<!-- doc-test-declaration -->
```csharp
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Respire.Serialization;

public sealed class ForwardingSerializer(IRespireSerializer inner) : IRespireSerializer
{
    [RequiresUnreferencedCode("The wrapped serializer may use reflection.")]
    [RequiresDynamicCode("The wrapped serializer may require runtime code generation.")]
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
        => inner.Serialize(destination, value);

    [RequiresUnreferencedCode("The wrapped serializer may use reflection.")]
    [RequiresDynamicCode("The wrapped serializer may require runtime code generation.")]
    public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        => inner.Deserialize<T>(payload);

    [RequiresUnreferencedCode("The wrapped serializer may use reflection.")]
    [RequiresDynamicCode("The wrapped serializer may require runtime code generation.")]
    public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
        => inner.Serialize(destination, type, value);

    [RequiresUnreferencedCode("The wrapped serializer may use reflection.")]
    [RequiresDynamicCode("The wrapped serializer may require runtime code generation.")]
    public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
        => inner.Deserialize(type, payload);
}
```

The annotations keep the interface's trimming and NativeAOT warnings. When the app requires AOT
safety, use a source-generated serializer as shown above.

## Zero-copy leased reads

For large or hot-path payloads, read into pooled memory:

```csharp
using RespireLease lease = await redis.Strings.GetLeaseAsync("blob:4mb");

if (!lease.IsNull)
{
    Process(lease.Span);
}
```

The memory is valid only until `Dispose`.

## Streaming large reads

`GetStreamAsync` exposes a Redis bulk value as a readable stream without a value-sized managed
array:

```csharp
await using Stream? value = await redis.Strings.GetStreamAsync("archive:latest");
if (value is not null)
{
    await using var destination = File.Create("archive.bin");
    await value.CopyToAsync(destination);
}
```

- **Always dispose the stream**, including when you stop early. Later replies on the same
  connection wait until the stream is consumed or disposed; an abandoned stream stalls them all.
- Respire buffers through a bounded 64 KiB pipe. If you read slowly, the socket reader pauses.
- Use another client connection for long reads that must not delay ordinary commands.
- Time spent waiting for you does not count toward the idle-read timeout. Graceful connection
  retirement waits for the stream to finish.
- The token passed to `GetStreamAsync` stays active for the stream's life. Canceling it fails pending
  reads and abandons the rest of the payload. You can also pass tokens to individual read or copy
  calls.
- Streaming reads bypass the [client-side cache](client-side-caching.md).

## Expiry without sentinels

Redis uses negative TTL values for missing and persistent keys. Respire returns `RespireTtlResult` instead:

```csharp
RespireTtlResult expiry = await redis.Keys.ExpiryAsync("session:42");

if (!expiry.Exists) { /* missing */ }
else if (!expiry.HasExpiry) { /* persistent */ }
else Console.WriteLine(expiry.TimeToLive);
```
