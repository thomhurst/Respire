using System.Text;
using BenchmarkDotNet.Attributes;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Bulk-string aggregate parsing, typed decoding, and disposal under pipeline pool pressure.</summary>
[MemoryDiagnoser]
public class AggregatePayloadBenchmarks
{
    private const int PipelineDepth = 50;
    private const int LargeCount = 2;
    private const int LargeValueLength = 1024 * 1024;
    private const int SparseCount = 4096;
    private const long SparseChecksum = (SparseCount - 2) * 42L + 6;

    [Params(100)]
    public int Count { get; set; }

    private byte[] _mget = null!;
    private byte[] _hgetall = null!;
    private byte[] _largeMget = null!;
    private byte[] _largeHgetall = null!;
    private byte[] _sparse = null!;
    private readonly RespValue[] _replies = new RespValue[PipelineDepth];

    [GlobalSetup]
    public void Setup()
    {
        var values = new StringBuilder($"*{Count}\r\n");
        var pairs = new StringBuilder($"%{Count}\r\n");
        for (var index = 0; index < Count; index++)
        {
            // Fixed-width, unique ASCII fields exercise dictionary materialization without duplicate keys.
            AppendBulk(values, $"value-{index:D6}");
            AppendBulk(pairs, $"field-{index:D6}");
            AppendBulk(pairs, $"value-{index:D6}");
        }
        _mget = Encoding.ASCII.GetBytes(values.ToString());
        _hgetall = Encoding.ASCII.GetBytes(pairs.ToString());
        _largeMget = LargeFrame(map: false);
        _largeHgetall = LargeFrame(map: true);
        _sparse = Encoding.ASCII.GetBytes($"*{SparseCount}\r\n$3\r\ntag\r\n"
            + string.Concat(Enumerable.Repeat(":42\r\n", SparseCount - 2)) + "$3\r\nend\r\n");
        if (MGet() != Count * 12L || HGetAll() != Count * 24L
            || RetainedMGet() != PipelineDepth * Count * 12L
            || RetainedHGetAll() != PipelineDepth * Count * 24L
            || MGetLargeValues() != LargeCount * (long)LargeValueLength
            || HGetAllLargeValues() != LargeCount * (LargeValueLength + 12L)
            || SparseMixed() != SparseChecksum || RetainedSparseMixed() != PipelineDepth * SparseChecksum)
            throw new InvalidOperationException("Aggregate payload benchmark fixture did not decode correctly.");
    }

    private static void AppendBulk(StringBuilder frame, string value)
        => frame.Append('$').Append(value.Length).Append("\r\n").Append(value).Append("\r\n");

    private static byte[] LargeFrame(bool map)
    {
        var frame = new StringBuilder($"{(map ? '%' : '*')}{LargeCount}\r\n");
        var payload = new string('x', LargeValueLength);
        for (var index = 0; index < LargeCount; index++)
        {
            if (map) AppendBulk(frame, $"field-{index:D6}");
            AppendBulk(frame, payload);
        }
        return Encoding.ASCII.GetBytes(frame.ToString());
    }

    [Benchmark]
    public long MGet()
    {
        using var reply = Parse(_mget);
        return Decode(reply, map: false);
    }

    [Benchmark]
    public long HGetAll()
    {
        using var reply = Parse(_hgetall);
        return Decode(reply, map: true);
    }

    [Benchmark]
    public long MGetLargeValues()
    {
        using var reply = Parse(_largeMget);
        return Decode(reply, map: false);
    }

    [Benchmark]
    public long HGetAllLargeValues()
    {
        using var reply = Parse(_largeHgetall);
        return Decode(reply, map: true);
    }

    [Benchmark(OperationsPerInvoke = PipelineDepth)]
    public long RetainedMGet() => Retained(_mget, map: false);

    [Benchmark(OperationsPerInvoke = PipelineDepth)]
    public long RetainedHGetAll() => Retained(_hgetall, map: true);

    [Benchmark]
    public long SparseMixed()
    {
        using var reply = Parse(_sparse);
        return DecodeSparse(reply);
    }

    [Benchmark(OperationsPerInvoke = PipelineDepth)]
    public long RetainedSparseMixed()
    {
        var parsed = 0;
        try
        {
            for (; parsed < _replies.Length; parsed++) _replies[parsed] = Parse(_sparse);
            long checksum = 0;
            for (var index = 0; index < parsed; index++) checksum += DecodeSparse(_replies[index]);
            return checksum;
        }
        finally
        {
            for (var index = 0; index < parsed; index++)
            {
                _replies[index].Dispose();
                _replies[index] = default;
            }
        }
    }

    private static long DecodeSparse(in RespValue reply)
    {
        long checksum = 0;
        foreach (var value in reply.AsArray())
            checksum += value.Type == RespDataType.Integer ? value.AsInteger() : value.AsSpan().Length;
        return checksum;
    }

    private long Retained(byte[] frame, bool map)
    {
        var parsed = 0;
        try
        {
            // Replies remain owned until the pipeline is drained, as on the receive/consumer handoff.
            for (; parsed < _replies.Length; parsed++) _replies[parsed] = Parse(frame);
            long checksum = 0;
            for (var index = 0; index < parsed; index++) checksum += Decode(_replies[index], map);
            return checksum;
        }
        finally
        {
            for (var index = 0; index < parsed; index++)
            {
                _replies[index].Dispose();
                _replies[index] = default;
            }
        }
    }

    private static RespValue Parse(byte[] frame)
    {
        var pos = 0;
        if (RespParser.TryParseValue(frame, ref pos, out var reply) == RespParseStatus.Done && pos == frame.Length)
            return reply;
        reply.Dispose();
        throw new InvalidOperationException("Incomplete aggregate benchmark frame.");
    }

    private static long Decode(in RespValue reply, bool map)
    {
        long checksum = 0;
        if (map)
        {
            foreach (var pair in ResponseReader.StringMap(reply)) checksum += pair.Key.Length + pair.Value.Length;
        }
        else
        {
            foreach (var value in ResponseReader.NullableStringArray(reply)) checksum += value?.Length ?? 0;
        }
        return checksum;
    }
}
