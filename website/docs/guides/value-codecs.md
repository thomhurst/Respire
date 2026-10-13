---
title: Value compression codecs
description: Compress serializer-backed values with framed Brotli, DEFLATE, LZ4, or Zstandard codecs.
---

# Value compression codecs

Wrap your serializer in `RespireValueCodecSerializer` to frame and optionally compress
**serializer-backed** values. Codecs are opt-in; without the decorator, nothing about the client or
the stored format changes.

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

| Codec | Package | Default level |
| --- | --- | --- |
| `BrotliValueCodec` | `Respire` | Quality 4 (range 0–11) |
| `DeflateValueCodec` (raw DEFLATE) | `Respire` | `CompressionLevel.Fastest` |
| `Lz4ValueCodec` | `Respire.Compression.Lz4` | See [LZ4](#optional-lz4-package) |
| `ZstdValueCodec` | `Respire.Compression.Zstd` | 3 |

| `RespireValueCodecOptions` | Default | Meaning |
| --- | --- | --- |
| `MinimumLength` | 1 KiB | Compression threshold. |
| `MaximumDecodedLength` | 8 MiB | Per-value ceiling on decoded bytes. |
| `FrameVersion` | 2 | Frame format to write; see [Frame and bounds](#frame-and-bounds). |

- Settings are captured at construction. Codec instances are thread-safe and reusable.
- Above the threshold, a codec keeps compression only if the result is smaller. Small, empty, and
  incompressible values use the same frame with an uncompressed payload.
- Framing adds 18 bytes, so a small value can grow.
- Encoding and decoding cost CPU and allocate owned buffers. No throughput gain is promised; see
  [Measuring value codec costs](./value-codec-benchmarks.md).
- Objects the inner serializer returns must own their data, because decoding uses scratch buffers
  that are reused.
- `MaximumDecodedLength` does not bound serializer buffering, compression workspace, or peak memory.

## Where codecs apply

The decorator applies wherever Respire uses `IRespireSerializer`:

- Non-primitive typed writes and reads, their batch and transaction methods, and serializer-backed
  `RespireResult.As<T>()`.
- Deferred writes serialize at enqueue time. Key prefixes change keys only.
- Client-side caching keeps encoded values; typed hits deserialize into independent objects.

It does not apply to:

- Primitive and raw paths: strings, byte arrays, characters, booleans, and numbers.
- Arguments bound as `RespireValue`, raw `ExecuteAsync` arguments, `AsBytes`, `AsSpan`, and string
  accessors.
- Missing values, which return the normal missing result without calling a decoder.

The decorator forwards both generic and runtime-type serializer methods, so it can wrap a custom
binary serializer or `SystemTextJsonSerializer.FromContext(...)` without adding reflection. The
existing trim/AOT annotations apply. Custom serializers and codecs must support concurrent calls and
must not retain input spans.

To compress raw bytes, call the codec yourself:

```csharp
using Respire.Compression;

IRespireValueCodec codec = new DeflateValueCodec();
byte[] encoded = codec.Encode(new byte[] { 0, 255, 128 });
await redis.SetAsync<byte[]>("binary", encoded);
byte[]? stored = await redis.Strings.GetBytesAsync("binary");
byte[]? decoded = stored is null ? null : codec.Decode(stored);
```

- The one-argument `Encode` and `Decode` return caller-owned arrays. The `IBufferWriter<byte>`
  overloads append to your storage and advance it only on success. A failed decode may have changed
  uncommitted writer memory.
- Custom implementations can implement only the array members; the default destination overloads
  copy their results. Implement the destination overloads to avoid that allocation.
- Calling a codec does not make the client decode other raw reads. For a collection that accepts
  only raw writes, serialize through the decorated serializer before enqueueing; the typed reader
  then uses the same decorator.

## Interoperability and migration

- Every codec-managed value is framed, even when uncompressed. Built-in decoders reject unframed
  legacy values, unknown frame versions, and another codec's compressed algorithm ID with
  `InvalidDataException`.
- Any built-in codec can read uncompressed frames.
- Changing the algorithm does **not** migrate stored values. Use a new key namespace, or read with
  the old decoder and rewrite with the new encoder. Other writers must use the same frame and
  serializer.
- Do not use Redis numeric operations, substring edits, JSON/module commands, or scripts that expect
  plain text on codec-framed values. Raw and primitive paths bypass the decorator, so they stay
  suitable for numeric operations.
- Collection member equality is byte equality. Serializer settings, threshold, algorithm, level, and
  runtime compression implementation can change the bytes. Keep them consistent for member
  lookup and removal, or store codec values under stable identifiers. Frames do not give canonical or
  stable compressed bytes across runtime versions.
- Distributed-cache compression is separate: set `RespireCacheOptions.ValueCodec` (also used for
  HybridCache L2). A client serializer does not compress the distributed cache's payload field. See
  [Microsoft caching](../integrations/caching.md).

## Optional LZ4 package

Install `Respire.Compression.Lz4` to use `Lz4ValueCodec`. It implements the same contract as Brotli
and Deflate and keeps its K4os dependency out of the core package.

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

- Level `0` is fast compression; `3`–`12` are increasing high-compression settings. Other levels are
  rejected. Decoding does not depend on the level.
- Compressed values use algorithm ID `3` with one raw LZ4 block, not an LZ4 frame stream or a K4os
  pickle. Only an LZ4 decoder can read them.
- No dictionary or native library is needed.
- Uses [K4os.Compression.LZ4 1.3.8](https://www.nuget.org/packages/K4os.Compression.LZ4/1.3.8)
  ([MIT license](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/LICENSE)), which
  uses unsafe managed code internally. Its
  [block APIs](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/src/K4os.Compression.LZ4/LZ4Codec.cs)
  write only into caller-provided storage.

## Optional Zstandard package

Install `Respire.Compression.Zstd` to use `ZstdValueCodec` with the same codec and serializer
contracts.

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

- Levels range from `-131072` to `22`. The default is `3`, and `0` also means `3`. Negative levels
  trade ratio for speed; measure with your payloads. Decoding does not depend on the level.
- Algorithm ID `4` holds exactly one ordinary Zstandard frame. Concatenated or skippable frames,
  trailing bytes, and external dictionaries are not supported. Frame errors surface as
  `InvalidDataException`.
- Uses [ZstdSharp.Port 0.8.8](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8)
  ([MIT license](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/LICENSE)): managed code with unsafe
  internals and unmanaged context memory. No native zstd binary is needed.
- The codec is thread-safe and needs no disposal.
- **Retained memory:** shared pools keep up to eight idle compression and eight decompression
  contexts (fewer on machines with fewer processors). With levels 1–22 on x64, this measured about
  14.4 MiB of retained workspace. Values larger than 64 KiB use unpooled contexts. Higher levels can
  need more time and workspace, and `MaximumDecodedLength` does not bound it.

## Frame and bounds

Versions 1 and 2 use this byte layout (offsets exclude RESP framing):

| Offset | Length | Meaning |
| --- | --- | --- |
| 0 | 4 | Magic bytes `52 56 43 00` (`RVC` followed by NUL) |
| 4 | 1 | Frame version: `2` by default; `1` remains supported |
| 5 | 1 | Algorithm: `0` uncompressed, `1` Brotli, `2` raw DEFLATE, `3` raw LZ4 block, `4` Zstandard frame |
| 6 | 4 | Original length, unsigned little-endian |
| 10 | 8 | Encoded payload checksum: little-endian XxHash3 64-bit (seed zero) in version 2; first eight SHA-256 bytes in version 1 |
| 18 | remaining | Encoded payload |

- The checksum detects accidental changes and truncation before decompression. It does not
  authenticate data or cover the header.
- Readers accept both versions, whatever the writer's configured version.

**Rolling upgrade:** old readers reject version 2.

1. Set `new RespireValueCodecOptions { FrameVersion = 1 }` on every upgraded writer.
2. Once all readers support version 2, switch writers to the default.
3. Existing version 1 values need no rewrite.

Before rolling back to old readers, remove or rewrite version 2 values as version 1. Changing the
write setting does not migrate stored values.

Bounds:

- A compressed payload must be smaller than the declared original; an uncompressed payload must
  match it exactly.
- The declared length is checked against `MaximumDecodedLength` before allocation and must match
  the actual output. The limit applies to serialized bytes, not .NET object size.
- Corrupt frames and size violations fail locally. Commands are not retried for conversion failures.
- A checksum-valid frame can allocate its declared size before decompression rejects malformed data.
  The 8 MiB default is a per-value ceiling, not a total memory budget or Redis limit. Set the
  smallest limit that fits your data, and allow for concurrent reads.

### Custom codecs

Derive from the public `RespireValueCodec` base class. You implement compression and bounded
decompression; the base handles thresholds, ownership, framing, and checksums.

- Use algorithm IDs 16–255, coordinated between your readers and writers. IDs 0–15 are reserved;
  the protected constructor throws `ArgumentOutOfRangeException` for them.
- Override the protected `TryCompress` span overload to avoid an array copy. Its destination is
  shorter than the input; return false when the output does not fit.
- There is no decoder registry or automatic fallback between algorithms.

Implementation references: [.NET BrotliEncoder](https://learn.microsoft.com/dotnet/api/system.io.compression.brotliencoder),
[BrotliDecoder](https://learn.microsoft.com/dotnet/api/system.io.compression.brotlidecoder), and
[DeflateStream](https://learn.microsoft.com/dotnet/api/system.io.compression.deflatestream).
