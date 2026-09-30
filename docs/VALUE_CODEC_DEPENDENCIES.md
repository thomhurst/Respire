# Optional codec dependency audit

## LZ4

`Respire.Compression.Lz4` references `K4os.Compression.LZ4` 1.3.8, the latest stable
NuGet release checked on 2026-09-30. The upstream repository is not archived; its
reported most recent push was 2026-03-28. Release age alone does not establish a
support commitment, and this audit makes no promise about future maintenance.

- [NuGet package and supported frameworks](https://www.nuget.org/packages/K4os.Compression.LZ4/1.3.8)
- [Pinned source, tag 1.3.8](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/tree/f5a25b7d72e2e41550fe20662597169ff11c3b60)
- [MIT license](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/LICENSE)
- [Block API source](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/src/K4os.Compression.LZ4/LZ4Codec.cs)
- [Compression levels](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/src/K4os.Compression.LZ4/LZ4Level.cs)

The dependency is consumed as a NuGet dependency, not copied into Respire source.
The upstream MIT license applies to that dependency. Only the optional project references it;
core Respire does not gain a package dependency. The core assembly grants the optional
assembly access to the existing reserved-ID constructor, so it can use ID 3 without
opening reserved IDs to arbitrary public subclasses.

The implementation uses `LZ4Codec.Encode(ReadOnlySpan<byte>, Span<byte>, LZ4Level)`
and `Decode(ReadOnlySpan<byte>, Span<byte>)`. Encode reports insufficient destination
space with a negative result. Decode invokes the safe full-block decoder and returns
the decoded byte count or a negative failure result. Respire requires that count to
equal the already validated frame length; it never uses partial decoding or trusts a
length embedded in an external LZ4 stream. Compression attempts receive less space
than the original value, so incompressible data falls back to algorithm 0 without an
expansion buffer. The shared frame validates size and checksum before decoding.

This is managed code with unsafe internals, not a native P/Invoke dependency. The public
Respire API exposes integer compression levels rather than dependency-specific types.
Level 0 and levels 3–12 are accepted. Raw LZ4 blocks, LZ4 frame streams, and K4os pickles
are different formats: reserved ID 3 denotes only a raw block inside a version 1
Respire frame. Cross-level decoding, an independent block fixture, trailing/truncated
input, checksum-valid malformed blocks, size limits, and writer commit behavior are
covered by tests. Performance comparisons remain separate in #527.
