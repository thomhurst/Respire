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

    [Params(100)]
    public int Count { get; set; }

    private byte[] _mget = null!;
    private byte[] _hgetall = null!;
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
        if (MGet() != Count * 12L || HGetAll() != Count * 24L
            || RetainedMGet() != PipelineDepth * Count * 12L
            || RetainedHGetAll() != PipelineDepth * Count * 24L)
            throw new InvalidOperationException("Aggregate payload benchmark fixture did not decode correctly.");
    }

    private static void AppendBulk(StringBuilder frame, string value)
        => frame.Append('$').Append(value.Length).Append("\r\n").Append(value).Append("\r\n");

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

    [Benchmark(OperationsPerInvoke = PipelineDepth)]
    public long RetainedMGet() => Retained(_mget, map: false);

    [Benchmark(OperationsPerInvoke = PipelineDepth)]
    public long RetainedHGetAll() => Retained(_hgetall, map: true);

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
