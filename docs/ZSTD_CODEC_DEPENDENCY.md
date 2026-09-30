# Zstandard codec dependency audit

`Respire.Compression.Zstd` uses `ZstdSharp.Port` 0.8.8, the latest stable NuGet
release checked on 2026-09-30. The repository is not archived and reported its most
recent push on 2026-09-25. Those observations do not promise future support.

- [Package and framework assets](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8)
- [Pinned source](https://github.com/oleg-st/ZstdSharp/tree/2cd0c019693bc786a5fe5c3be94e107b24e7267e)
- [MIT license](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/LICENSE)
- [Compressor](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/Compressor.cs)
- [Decompressor](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/Decompressor.cs)
- [Context ownership](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/SafeHandles.cs)
- [Decoder implementation](https://github.com/oleg-st/ZstdSharp/blob/0.8.8/src/ZstdSharp/Unsafe/ZstdDecompress.cs)

The dependency is consumed through NuGet rather than copied into this repository.
Its MIT license applies to the package. Only the optional project references it;
core Respire gains no package dependency. The package supplies net8.0/net9.0 assets
usable by our net8.0/net10.0 targets, with no further runtime package dependencies
for those assets. It is a C# port of Zstandard 1.5.7, with unsafe internals and
unmanaged context memory, but requires no native zstd binary or runtime-specific
native deployment. Normal .NET builds and tests do not establish a separate
Native AOT compatibility result.

The selected span APIs are `Compressor.TryWrap(ReadOnlySpan<byte>, Span<byte>, out int)`
and `Decompressor.Unwrap(ReadOnlySpan<byte>, Span<byte>)`. The first returns false
only for insufficient output space; other errors throw. The second writes into
the caller's existing destination and reports its decoded byte count. We do not
use the allocating overload that derives an allocation from frame metadata.
Both wrappers own SafeHandles and implement IDisposable. Each operation creates
and disposes its own context, including exceptional paths, so a codec instance
holds no disposable state and can serve concurrent calls. This choice incurs
context creation costs; it makes no allocation or throughput promise.

The shared Respire frame checks its declared decoded length against the configured
maximum before allocating/requesting output. Compression receives scratch space
shorter than its input and falls back to the shared uncompressed frame when it
does not fit. The direct decoder uses the provided output as history, rather than
allocating a streaming window from the untrusted frame. Output size limits are not
an overall process-memory or compression-time budget; encoder workspace depends
on input and compression level, and concurrent calls own separate workspaces.

Reserved algorithm ID 4 means one ordinary Zstandard frame without an external
dictionary inside an RVC version 1 frame. The dependency normally accepts multiple
ordinary/skippable frames. A small pinned-span call to
`Methods.ZSTD_findFrameCompressedSize` verifies that exactly one ordinary frame
occupies the payload before decoding. The helper retains no pointers and performs
no P/Invoke. Decode errors become InvalidDataException and the decoded count must
match the validated outer length. Compression levels are -131072 through 22;
zero selects the default, and decoding is independent of the encoder's level.

Tests include a hand-authored RLE frame based on the
[Zstandard format specification](https://github.com/facebook/zstd/blob/v1.5.7/doc/zstd_compression_format.md),
concatenated and skippable frames, corruption, length mismatches, destination commit
behavior, and the shared typed/cache integration cases. Benchmark acceptance remains
separate in #527.
