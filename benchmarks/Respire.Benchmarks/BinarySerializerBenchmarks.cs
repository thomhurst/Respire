using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MemoryPack;
using MessagePack;
using ProtoBuf;
using Respire.Serialization;
using Respire.Serialization.MemoryPack;
using Respire.Serialization.MessagePack;
using Respire.Serialization.Protobuf;

namespace Respire.Benchmarks;

/// <summary>Compares adapter CPU and allocations without Redis/network noise.</summary>
[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class BinarySerializerBenchmarks
{
    private readonly IRespireSerializer _json = new RespireSystemTextJsonSerializer();
    private readonly IRespireSerializer _messagePack = new RespireMessagePackSerializer();
    private readonly IRespireSerializer _memoryPack = new RespireMemoryPackSerializer();
    private readonly IRespireSerializer _protobuf = new RespireProtobufSerializer();
    private readonly ArrayBufferWriter<byte> _writer = new();
    private BinarySerializerPayload _value = null!;
    private byte[] _jsonBytes = null!, _messagePackBytes = null!, _memoryPackBytes = null!, _protobufBytes = null!;

    [Params(64, 4096)] public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _value = new() { Name = new string('x', Length), Age = 42 };
        _jsonBytes = Seed(_json);
        _messagePackBytes = Seed(_messagePack);
        _memoryPackBytes = Seed(_memoryPack);
        _protobufBytes = Seed(_protobuf);
    }

    private byte[] Seed(IRespireSerializer serializer)
    {
        Write(serializer);
        var bytes = _writer.WrittenSpan.ToArray();
        var value = serializer.Deserialize<BinarySerializerPayload>(bytes);
        var runtime = (BinarySerializerPayload?)serializer.Deserialize(typeof(BinarySerializerPayload), bytes);
        if (value?.Name != _value.Name || value.Age != _value.Age || runtime?.Name != _value.Name || runtime.Age != _value.Age)
            throw new InvalidOperationException("Serializer changed the benchmark payload.");
        return bytes;
    }

    private int Write(IRespireSerializer serializer)
    {
        _writer.Clear();
        serializer.Serialize(_writer, _value);
        return _writer.WrittenCount;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Write")] public int JsonWrite() => Write(_json);
    [Benchmark, BenchmarkCategory("Write")] public int MessagePackWrite() => Write(_messagePack);
    [Benchmark, BenchmarkCategory("Write")] public int MemoryPackWrite() => Write(_memoryPack);
    [Benchmark, BenchmarkCategory("Write")] public int ProtobufWrite() => Write(_protobuf);
    [Benchmark(Baseline = true), BenchmarkCategory("Read")] public BinarySerializerPayload? JsonRead() => _json.Deserialize<BinarySerializerPayload>(_jsonBytes);
    [Benchmark, BenchmarkCategory("Read")] public BinarySerializerPayload? MessagePackRead() => _messagePack.Deserialize<BinarySerializerPayload>(_messagePackBytes);
    [Benchmark, BenchmarkCategory("Read")] public BinarySerializerPayload? MemoryPackRead() => _memoryPack.Deserialize<BinarySerializerPayload>(_memoryPackBytes);
    [Benchmark, BenchmarkCategory("Read")] public BinarySerializerPayload? ProtobufRead() => _protobuf.Deserialize<BinarySerializerPayload>(_protobufBytes);
}

[MemoryPackable, MessagePackObject, ProtoContract]
public partial class BinarySerializerPayload
{
    [Key(0), ProtoMember(1)] public string Name { get; set; } = "";
    [Key(1), ProtoMember(2)] public int Age { get; set; }
}
