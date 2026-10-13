# Optional codec dependency audit

Frame format rules for built-in value codecs, and the audit of the LZ4 dependency. Compression
dependencies belong only in their optional package, never in core Respire. The Zstandard audit is in
[ZSTD_CODEC_DEPENDENCY.md](ZSTD_CODEC_DEPENDENCY.md).

## Frame compatibility

Built-in codecs write version 2 frames by default and always decode version 1.

| | Version 1 | Version 2 |
| --- | --- | --- |
| Header | 18 bytes, same algorithm IDs | 18 bytes, same algorithm IDs |
| Checksum | First eight SHA-256 bytes of the encoded payload | Seed-zero XxHash3 64-bit, little-endian |

Checksums detect accidental payload corruption. They do not authenticate and do not cover the header.

Old readers cannot decode version 2. For a rolling upgrade:

1. Configure every new writer with `new RespireValueCodecOptions { FrameVersion = 1 }`.
2. When all readers support version 2, switch writers to the default.

Upgraded readers accept both versions, and existing values need no rewrite. Before rolling back to an
old reader, remove or rewrite version 2 values as version 1; changing the write setting does not
migrate stored values.

## LZ4

`Respire.Compression.Lz4` references `K4os.Compression.LZ4` 1.3.8, the latest stable release on
2026-09-30. The upstream repository is not archived (last push 2026-03-28). This audit makes no promise
about future maintenance.

- [NuGet package and supported frameworks](https://www.nuget.org/packages/K4os.Compression.LZ4/1.3.8)
- [Pinned source, tag 1.3.8](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/tree/f5a25b7d72e2e41550fe20662597169ff11c3b60)
- [MIT license](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/LICENSE)
- [Block API source](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/src/K4os.Compression.LZ4/LZ4Codec.cs)
- [Compression levels](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/src/K4os.Compression.LZ4/LZ4Level.cs)

Packaging:

- Consumed as a NuGet dependency under its MIT license, not copied into source. Only the optional
  project references it.
- Managed code with unsafe internals; no native P/Invoke.
- The core assembly grants the optional assembly access to the reserved-ID constructor, so it can use
  ID 3 without opening reserved IDs to public subclasses.
- The public API takes integer levels, not dependency types. Accepted levels: 0 and 3–12.

Implementation:

- Uses `LZ4Codec.Encode(ReadOnlySpan<byte>, Span<byte>, LZ4Level)` and
  `Decode(ReadOnlySpan<byte>, Span<byte>)`. Both signal failure with a negative result; Decode uses
  the safe full-block decoder.
- The decoded count must equal the already validated frame length. No partial decoding; no trust in
  a length embedded in an external LZ4 stream.
- Compression gets less space than the input, so incompressible data falls back to algorithm 0
  without an expansion buffer.
- The shared frame validates size and checksum before decoding.
- Reserved ID 3 means only a raw LZ4 block inside a versioned Respire frame. LZ4 frame streams and
  K4os pickles are different formats.

Tests cover cross-level decoding, an independent block fixture, trailing and truncated input,
checksum-valid malformed blocks, size limits and writer commit behavior. Results are in the
[value codec benchmarks guide](../website/docs/guides/value-codec-benchmarks.md).
