# Value compression codecs

Value codecs are opt-in. Configure `RespireValueCodecSerializer` around your existing
serializer to frame and optionally compress **serializer-backed** values. No client
setting or wire format changes when this decorator is absent.

```csharp
using Respire.Compression;
using Respire.Serialization;

var codec = new BrotliValueCodec(new RespireValueCodecOptions
{
    MinimumLength = 1024,
    MaximumDecodedLength = 16 * 1024 * 1024,
});
await using var compressed = RespireClient.Create(new RespireOptions
{
    Endpoints = [new RespireEndpoint("localhost", 6379)],
    Serializer = new RespireValueCodecSerializer(RespireSerializer.Default, codec),
});
await compressed.SetAsync("profile", new Dictionary<string, string>
{
    ["description"] = new string('x', 4096),
});
var profile = await compressed.GetAsync<Dictionary<string, string>>("profile");
```

`BrotliValueCodec` defaults to quality 4 (valid range 0–11). `DeflateValueCodec` uses
raw DEFLATE and defaults to `CompressionLevel.Fastest`. Both use the .NET compression
libraries without extra compression packages. The default threshold is 1 KiB and the
default maximum original/decoded length is 8 MiB. Settings are captured when the codec
is constructed. Instances are reusable across concurrent operations.

Above the threshold, a codec tries compression and keeps it only if its payload is
smaller. Below the threshold, empty values, and incompressible values use the same frame
with an uncompressed payload. Framing adds 18 bytes to either representation; a small
value can therefore grow compared with storage without a codec. Encoding and decoding
allocate owned buffers and consume CPU. See the [focused benchmark design and reproduction
guide](./value-codec-benchmarks.md) for equivalent payloads, allocation scope, and threshold
comparisons. No throughput improvement is promised.

The serializer decorator buffers the complete serialized value in rented storage, then
asks the codec to write into its destination. Built-in codecs construct the final frame
directly in that writer, avoiding an intermediate frame array and its copy. Decode also
uses rented scratch storage before calling the inner serializer. Every scratch rental is
cleared and returned after success or failure; returned objects must own their data.
Brotli compression writes into bounded rented storage without an intermediate compressed
array. DEFLATE compression uses the compatibility fallback, which copies its compressed
array into scratch storage. DEFLATE compression buffers stream output and copies it
into an owned array; decoding copies compressed input into a stream-backed array.
These allocations are part of the current opt-in cost. The decoded-length limit does
not bound serializer buffering, compression workspace, or total peak memory.

## Where codecs apply

The decorator participates wherever Respire already uses `IRespireSerializer`:
non-primitive typed writes and reads, corresponding batch/transaction methods, and
serializer-backed `RespireResult.As<T>()`. Deferred writes own their serialized snapshot
at enqueue time. Prefixes change keys only. Client-side caching retains encoded values;
typed cache hits deserialize into independent objects.

Existing primitive and raw-value paths still bypass serialization, including strings,
byte arrays, characters, booleans, and numbers. Overload resolution remains significant:
an argument bound as `RespireValue` is raw even if explicitly serializing its .NET type
would use the serializer. Raw `ExecuteAsync` arguments, `AsBytes`, `AsSpan`, and string
accessors remain raw. Missing Redis values still return the ordinary missing result
without invoking a decoder.

The decorator forwards both generic and runtime-type serializer methods. It can wrap
a custom binary serializer or `SystemTextJsonSerializer.FromContext(...)`; it does not
introduce reflection-based dispatch. The existing serializer trim/AOT annotations apply.
Custom serializers and codecs must support concurrent calls and must not retain input spans.

For explicit compression of raw bytes, invoke the codec yourself:

```csharp
using Respire.Compression;

IRespireValueCodec codec = new DeflateValueCodec();
byte[] encoded = codec.Encode(new byte[] { 0, 255, 128 });
await redis.SetAsync<byte[]>("binary", encoded);
byte[]? stored = await redis.Strings.GetBytesAsync("binary");
byte[]? decoded = stored is null ? null : codec.Decode(stored);
```

The one-argument `IRespireValueCodec.Encode` and `Decode` return caller-owned arrays.
Their `IBufferWriter<byte>` overloads append to caller-supplied storage. Built-in
codecs write frames or decoded bytes directly into that storage and advance it only
after success. A failed decompression may have changed uncommitted writer memory;
it does not advance the destination. Existing custom implementations can implement
only the array members: default destination overloads copy those results. Implement
the destination overloads to avoid those fallback allocations.

Calling a codec
explicitly does not configure the client to decode other raw reads. For a collection
that only accepts raw writes, explicitly serialize through the decorated serializer
before enqueueing its value; the existing typed reader then uses the same decorator.

## Interoperability and migration

Every codec-managed value is framed, including uncompressed values. Built-in decoders
reject unframed legacy values, unknown frame versions, and another codec's compressed
algorithm ID with `InvalidDataException`. Uncompressed frames can be read by either
built-in codec. Changing the configured algorithm does **not** migrate existing
compressed values: use a new key namespace or explicitly read with the old decoder and
rewrite with the new encoder. Other writers must use the same frame and serializer contract.

Do not apply Redis numeric operations, substring edits, JSON/module operations, or
server-side scripts expecting plain serialized text to codec-framed values. Collection
member equality is byte equality: serializer settings, threshold, algorithm, quality,
and runtime compression implementation can change encoded bytes. Keep those settings
consistent for member lookup/removal, or store codec values under stable identifiers
instead of using them as identity-bearing members. Codec frames do not provide canonical
serialization or stable compressed bytes across runtime versions.

The default raw/primitive paths remain suitable for numeric operations because they
bypass this decorator. Distributed-cache compression is a separate opt-in through
`RespireCacheOptions.ValueCodec`, also used for HybridCache L2. Setting a client's serializer
does not compress the distributed cache's payload field. See [Microsoft caching](../integrations/caching.md)
for configuration, buffer APIs, and cache namespace migration.

## Optional LZ4 package

Install `Respire.Compression.Lz4` to use `Lz4ValueCodec`. Its K4os dependency stays out
of the core Respire package. The codec implements the same `IRespireValueCodec`
contract as Brotli and Deflate, including both array and destination overloads.

```csharp
using Respire.Compression;
using Respire.Compression.Lz4;
using Respire.Serialization;

var codec = new Lz4ValueCodec(new RespireValueCodecOptions
{
    MinimumLength = 1024,
    MaximumDecodedLength = 1024 * 1024
}, level: 0);
var serializer = new RespireValueCodecSerializer(RespireSerializer.Default, codec);
var options = new RespireOptions { Serializer = serializer };
byte[] frame = codec.Encode(new byte[4096]);
byte[] restored = codec.Decode(frame);
if (restored.Length != 4096) throw new InvalidOperationException("Round trip failed.");
```

Level `0` selects fast compression; levels `3`–`12` select increasing high-compression
settings. Other levels are rejected. Decoding does not depend on the encoder's level.
Small or incompressible values use the shared uncompressed frame. Compressed values use
algorithm ID `3` with one raw LZ4 block: neither an LZ4 frame stream nor a K4os pickle.
There is no dictionary or native-library requirement. Different algorithm decoders can
read shared uncompressed frames, but compressed LZ4 values require an LZ4 decoder.

The package uses [K4os.Compression.LZ4 1.3.8](https://www.nuget.org/packages/K4os.Compression.LZ4/1.3.8),
under its [MIT license](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/LICENSE).
Its bounded span [block APIs](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/src/K4os.Compression.LZ4/LZ4Codec.cs)
write only into caller-provided storage. The shared frame checks the declared length
before allocating output; a failed decode does not advance a destination writer.
The dependency uses unsafe managed code internally. No throughput, latency, or
zero-allocation claim is implied; measurements remain tracked in #527.

## Optional Zstandard package

Install `Respire.Compression.Zstd` to use `ZstdValueCodec` through the same
`IRespireValueCodec` and serializer contracts. Its dependency stays out of core Respire.

```csharp
using Respire.Compression;
using Respire.Compression.Zstd;
using Respire.Serialization;

var codec = new ZstdValueCodec(new RespireValueCodecOptions
{
    MinimumLength = 1024,
    MaximumDecodedLength = 1024 * 1024
}, level: 3);
var serializer = new RespireValueCodecSerializer(RespireSerializer.Default, codec);
var options = new RespireOptions { Serializer = serializer };
byte[] frame = codec.Encode(new byte[4096]);
byte[] restored = codec.Decode(frame);
if (restored.Length != 4096) throw new InvalidOperationException("Round trip failed.");
```

Levels range from `-131072` through `22`; the default is `3`, and `0` also selects
the dependency's default of `3`. Negative levels favor speed over compression ratio;
measure that tradeoff with your payloads. Decoder settings do not depend on the encoder's level.
Small or incompressible values retain the shared uncompressed frame. Algorithm ID
`4` contains exactly one ordinary Zstandard frame. Concatenated/skippable frames,
trailing bytes, and external dictionaries are not supported.

The package uses [ZstdSharp.Port 0.8.8](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8)
under its [MIT license](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/LICENSE).
This is managed code with unsafe internals and unmanaged context memory; no native
zstd binary is required. Each call owns and disposes its context before returning,
so the codec is thread-safe and requires no disposal. This has a per-call cost.
Higher levels can need more time and workspace. The decoded-length limit bounds
the value, not all compressor workspace or concurrent process memory. Choose levels
and limits for your workload; no performance claim is implied (#527).

Decoding writes directly into bounded output, checks the complete single-frame
input and exact decoded length, and does not advance a destination writer on failure.
Underlying frame errors surface as `InvalidDataException`. Arrays remain owned by
the caller, as with the built-in codecs.

## Frame and bounds

Version 1 uses this byte layout; offsets and length exclude any Redis RESP framing:

| Offset | Length | Meaning |
| --- | --- | --- |
| 0 | 4 | Magic bytes `52 56 43 00` (`RVC` followed by NUL) |
| 4 | 1 | Frame version, currently `1` |
| 5 | 1 | Algorithm: `0` uncompressed, `1` Brotli, `2` raw DEFLATE, `3` raw LZ4 block, `4` Zstandard frame |
| 6 | 4 | Original length, unsigned little-endian |
| 10 | 8 | First eight bytes of SHA-256 of the encoded payload |
| 18 | remaining | Encoded payload |

The checksum detects accidental changes and truncation before invoking a decompressor;
it does not authenticate data. SHA-256 uses the platform implementation without adding
a hashing dependency; truncation limits frame overhead to eight checksum bytes. This
choice does not claim a throughput advantage over noncryptographic checksums. Hashing
the payload consumes CPU on each encode and decode; #527 must include that cost.
A compressed payload must be smaller than the declared
original, while an uncompressed payload must have exactly that length. Output length is
checked against `MaximumDecodedLength` before allocation and must match the decompressor's
actual output. Corrupt frames and size violations fail locally; commands are not retried
because value conversion failed. The limit applies to serialized bytes, not .NET object size.

A checksum-valid frame can allocate its declared output size before decompression
rejects malformed data. The default 8 MiB is a per-value ceiling, not a total memory
budget or a Redis server limit. Set `MaximumDecodedLength` to the smallest application
value limit that fits your data, and account for concurrent reads when choosing it.

The public `RespireValueCodec` base class shares these framing rules with optional/custom
codecs. IDs 3 and 4 belong to the optional LZ4 and Zstandard packages. IDs 16–255 are available
for an application's custom codecs and must be coordinated between its readers and writers.
The protected constructor rejects IDs 0–15; built-in implementations use an internal
reserved-ID constructor, also available to explicitly trusted optional codec assemblies.
A custom codec implements compression and bounded decompression, while the base manages
thresholds, ownership, framing, and checksums. Override the protected `TryCompress` span
overload to avoid the owned-array fallback and its copy. Its destination is shorter than
the original input; return false when compressed output does not fit. There is no decoder registry or automatic
fallback between algorithms.

These are additive public types; existing client and facet interfaces gain no members.
Measured trade-offs remain a separate deliverable in
[#425](https://github.com/thomhurst/Respire/issues/425).

Implementation references: [.NET BrotliEncoder](https://learn.microsoft.com/dotnet/api/system.io.compression.brotliencoder),
[BrotliDecoder](https://learn.microsoft.com/dotnet/api/system.io.compression.brotlidecoder), and
[DeflateStream](https://learn.microsoft.com/dotnet/api/system.io.compression.deflatestream).
