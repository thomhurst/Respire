# Zstandard codec dependency audit

`Respire.Compression.Zstd` uses `ZstdSharp.Port` 0.8.8 for reserved algorithm ID 4. Only the optional
project references it. Decode only into caller-provided output sized from the validated Respire frame,
and accept exactly one ordinary Zstandard frame.

## Package

`ZstdSharp.Port` 0.8.8 was the latest stable release on 2026-09-30. The repository is not archived
(last push 2026-09-25). This does not promise future support.

- [Package and framework assets](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8)
- [Pinned source](https://github.com/oleg-st/ZstdSharp/tree/2cd0c019693bc786a5fe5c3be94e107b24e7267e)
- [MIT license](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/LICENSE)
- [Compressor](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/Compressor.cs)
- [Decompressor](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/Decompressor.cs)
- [Context ownership](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/SafeHandles.cs)
- [Decoder implementation](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/Unsafe/ZstdDecompress.cs)

Packaging:

- Consumed through NuGet under its MIT license, not copied. Core Respire gains no dependency.
- net8.0/net9.0 assets serve our net8.0/net10.0 targets, with no further runtime package dependencies.
- A C# port of Zstandard 1.5.7 with unsafe internals and unmanaged context memory. No native zstd
  binary or runtime-specific deployment.
- Normal builds and tests do not establish Native AOT compatibility.

## API use

- `Compressor.TryWrap(ReadOnlySpan<byte>, Span<byte>, out int)` returns false only for insufficient
  output space; other errors throw.
- `Decompressor.Unwrap(ReadOnlySpan<byte>, Span<byte>)` writes into the caller's destination and
  returns the decoded count. Do not use the overload that allocates from frame metadata.
- Each operation creates and disposes its own context (both wrappers own SafeHandles), including on
  exceptions. A codec instance holds no disposable state and can serve concurrent calls, at the cost
  of context creation. No allocation or throughput promise.

## Safety rules

- The Respire frame checks the declared decoded length against the configured maximum before
  requesting output.
- Compression gets scratch space shorter than its input and falls back to the uncompressed frame when
  the result does not fit.
- The decoder uses the provided output as history instead of allocating a streaming window from the
  untrusted frame.
- Output limits are not a process-memory or compression-time budget. Encoder workspace depends on
  input and level; concurrent calls own separate workspaces.
- ID 4 means one ordinary Zstandard frame with no external dictionary, inside a version 1 or 2
  Respire frame (see [frame compatibility](VALUE_CODEC_DEPENDENCIES.md#frame-compatibility)). The
  dependency accepts multiple ordinary or skippable frames, so a pinned-span call to
  `Methods.ZSTD_findFrameCompressedSize` checks that exactly one ordinary frame fills the payload. It
  keeps no pointers and does no P/Invoke.
- Decode errors become `InvalidDataException`. The decoded count must match the validated outer length.
- Levels are -131072 through 22; 0 selects the default. Decoding does not depend on the encoder's level.

## Tests

A hand-authored RLE frame based on the
[Zstandard format specification](https://github.com/facebook/zstd/blob/v1.5.7/doc/zstd_compression_format.md),
concatenated and skippable frames, corruption, length mismatches, destination commit behavior, and the
shared typed and cache integration cases. Results are in the
[value codec benchmarks guide](../website/docs/guides/value-codec-benchmarks.md).
