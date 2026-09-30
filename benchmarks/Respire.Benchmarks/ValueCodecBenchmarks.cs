using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Respire.Compression;

namespace Respire.Benchmarks;

/// <summary>Codec-only destination API costs for the same already-serialized bytes.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ValueCodecBenchmarks
{
    public enum PayloadPattern { RepeatedText, RandomBytes }

    [Params(64, 16384)]
    public int Length { get; set; }

    [Params(PayloadPattern.RepeatedText, PayloadPattern.RandomBytes)]
    public PayloadPattern Pattern { get; set; }

    [Params(0, 1024)]
    public int MinimumLength { get; set; }

    private byte[] _payload = null!;
    private string _payloadSha256 = null!;
    private ArrayBufferWriter<byte> _destination = null!;
    private IRespireValueCodec _brotli = null!;
    private IRespireValueCodec _deflate = null!;
    private IRespireValueCodec _lz4 = null!;
    private IRespireValueCodec _zstd = null!;
    private byte[] _brotliFrame = null!;
    private byte[] _deflateFrame = null!;
    private byte[] _lz4Frame = null!;
    private byte[] _zstdFrame = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[Length];
        if (Pattern == PayloadPattern.RandomBytes)
            new Random(527).NextBytes(_payload);
        else
        {
            ReadOnlySpan<byte> record = "tenant=demo;type=event;status=active;value=12345;\n"u8;
            for (var index = 0; index < _payload.Length; index++)
                _payload[index] = record[index % record.Length];
        }

        _payloadSha256 = Convert.ToHexString(SHA256.HashData(_payload));

        // All methods retain enough output capacity, without measuring buffer growth or
        // clearing different previous output lengths. Codec-owned scratch remains measured.
        _destination = new ArrayBufferWriter<byte>(Length + RespireValueCodec.HeaderLength);
        var options = new RespireValueCodecOptions { MinimumLength = MinimumLength };
        _brotli = new BrotliValueCodec(options);
        _deflate = new DeflateValueCodec(options);
        _lz4 = new Lz4ValueCodec(options);
        _zstd = new ZstdValueCodec(options);
        _brotliFrame = Validate("Brotli", _brotli);
        _deflateFrame = Validate("Deflate", _deflate);
        _lz4Frame = Validate("Lz4", _lz4);
        _zstdFrame = Validate("Zstd", _zstd);
        if (!RawEncode().Span.SequenceEqual(_payload) || !RawDecode().Span.SequenceEqual(_payload))
            throw new InvalidOperationException("The disabled path changed the raw value.");
        ReportSize("Raw", _payload, algorithm: null);
    }

    private byte[] Validate(string name, IRespireValueCodec codec)
    {
        var frame = codec.Encode(_payload);
        if (!Encode(codec).Span.SequenceEqual(frame)
            || !Decode(codec, frame).Span.SequenceEqual(_payload)
            || !codec.Decode(frame).AsSpan().SequenceEqual(_payload))
            throw new InvalidOperationException($"{name} array/destination round trip failed.");
        if (Length < MinimumLength && (frame[5] != 0 || frame.Length != Length + RespireValueCodec.HeaderLength))
            throw new InvalidOperationException("Below-threshold framing changed.");
        ReportSize(name, frame, frame[5]);
        return frame;
    }

    private void ReportSize(string codec, byte[] encoded, byte? algorithm)
    {
        // Emitted outside timing. The report generator verifies identical metadata across
        // launches and joins these exact sizes to each BDN time/allocation measurement.
        Console.WriteLine("VALUE_CODEC_SIZE " + JsonSerializer.Serialize(new
        {
            Length, Pattern = Pattern.ToString(), MinimumLength, Codec = codec,
            EncodedBytes = encoded.Length,
            RespBulkStringBytes = encoded.Length + encoded.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length + 5,
            Algorithm = algorithm,
            PayloadSha256 = _payloadSha256,
        }));
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Encode")]
    public ReadOnlyMemory<byte> RawEncode() => CopyRaw();
    [Benchmark, BenchmarkCategory("Encode")]
    public ReadOnlyMemory<byte> BrotliEncode() => Encode(_brotli);
    [Benchmark, BenchmarkCategory("Encode")]
    public ReadOnlyMemory<byte> DeflateEncode() => Encode(_deflate);
    [Benchmark, BenchmarkCategory("Encode")]
    public ReadOnlyMemory<byte> Lz4Encode() => Encode(_lz4);
    [Benchmark, BenchmarkCategory("Encode")]
    public ReadOnlyMemory<byte> ZstdEncode() => Encode(_zstd);

    [Benchmark(Baseline = true), BenchmarkCategory("Decode")]
    public ReadOnlyMemory<byte> RawDecode() => CopyRaw();
    [Benchmark, BenchmarkCategory("Decode")]
    public ReadOnlyMemory<byte> BrotliDecode() => Decode(_brotli, _brotliFrame);
    [Benchmark, BenchmarkCategory("Decode")]
    public ReadOnlyMemory<byte> DeflateDecode() => Decode(_deflate, _deflateFrame);
    [Benchmark, BenchmarkCategory("Decode")]
    public ReadOnlyMemory<byte> Lz4Decode() => Decode(_lz4, _lz4Frame);
    [Benchmark, BenchmarkCategory("Decode")]
    public ReadOnlyMemory<byte> ZstdDecode() => Decode(_zstd, _zstdFrame);

    private ReadOnlyMemory<byte> CopyRaw()
    {
        _destination.ResetWrittenCount();
        _destination.Write(_payload);
        return _destination.WrittenMemory;
    }

    private ReadOnlyMemory<byte> Encode(IRespireValueCodec codec)
    {
        _destination.ResetWrittenCount();
        codec.Encode(_payload, _destination);
        return _destination.WrittenMemory;
    }

    private ReadOnlyMemory<byte> Decode(IRespireValueCodec codec, byte[] frame)
    {
        _destination.ResetWrittenCount();
        codec.Decode(frame, _destination);
        return _destination.WrittenMemory;
    }
}
