using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Respire.Compression;

namespace Respire.Benchmarks;

/// <summary>Compares frame versions with compression disabled and identical destination storage.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ValueCodecChecksumBenchmarks
{
    [Params(64, 16384)]
    public int Length { get; set; }

    private readonly BrotliValueCodec _versionOne = new(new() { FrameVersion = 1, MinimumLength = int.MaxValue });
    private readonly BrotliValueCodec _versionTwo = new(new() { MinimumLength = int.MaxValue });
    private byte[] _payload = null!;
    private byte[] _frameOne = null!;
    private byte[] _frameTwo = null!;
    private ArrayBufferWriter<byte> _destination = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[Length];
        new Random(981).NextBytes(_payload);
        _frameOne = _versionOne.Encode(_payload);
        _frameTwo = _versionTwo.Encode(_payload);
        _destination = new ArrayBufferWriter<byte>(Length + RespireValueCodec.HeaderLength);
        if (_frameOne[4] != 1 || _frameTwo[4] != 2 || _frameOne[5] != 0 || _frameTwo[5] != 0
            || !EncodeVersionOne().Span.SequenceEqual(_frameOne)
            || !EncodeVersionTwo().Span.SequenceEqual(_frameTwo)
            || !DecodeVersionOne().Span.SequenceEqual(_payload)
            || !DecodeVersionTwo().Span.SequenceEqual(_payload))
            throw new InvalidOperationException("Codec frame comparison failed validation.");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Encode")]
    public ReadOnlyMemory<byte> EncodeVersionOne() => Encode(_versionOne);
    [Benchmark, BenchmarkCategory("Encode")]
    public ReadOnlyMemory<byte> EncodeVersionTwo() => Encode(_versionTwo);
    [Benchmark(Baseline = true), BenchmarkCategory("Decode")]
    public ReadOnlyMemory<byte> DecodeVersionOne() => Decode(_frameOne);
    [Benchmark, BenchmarkCategory("Decode")]
    public ReadOnlyMemory<byte> DecodeVersionTwo() => Decode(_frameTwo);

    private ReadOnlyMemory<byte> Encode(RespireValueCodec codec)
    {
        _destination.ResetWrittenCount();
        codec.Encode(_payload, _destination);
        return _destination.WrittenMemory;
    }

    private ReadOnlyMemory<byte> Decode(byte[] frame)
    {
        _destination.ResetWrittenCount();
        _versionTwo.Decode(frame, _destination);
        return _destination.WrittenMemory;
    }
}
