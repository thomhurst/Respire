using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Compares vector encoding after input validation and option construction, before transport.</summary>
[MemoryDiagnoser]
public class VectorEncodingBenchmarks
{
    private float[] _vector = null!;
    private WriteBuffer _buffer = null!;
    private VectorCommand _direct;
    private VectorCommand _values;
    private Cmd1N _intermediate;
    private RespireValue[] _intermediateArguments = null!;

    [Params(16, 1536)]
    public int Dimensions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(534);
        _vector = Enumerable.Range(0, Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        _vector[0] = 0f;
        _vector[1] = -0f;
        _buffer = new WriteBuffer(checked(Dimensions * 64 + 256));
        var verb = RespireCommands.VectorSet.VADD.Verb;
        RespireValue key = "vectors", member = "member";
        _direct = new(verb, key, _vector, RespireVectorEncoding.Fp32, [], [member]);
        _values = new(verb, key, _vector, RespireVectorEncoding.Values, [], [member]);
        _intermediateArguments = ["FP32", "", member];
        _intermediate = new(verb, key, _intermediateArguments);

        // Validate whole RESP frames and every FP32 bit outside timed operations.
        // Each method resets the buffer, so setup call order leaves no timed state dependency.
        var originalCapacity = _buffer.Capacity;
        var fp32Bytes = DirectFp32();
        var direct = _buffer.WrittenMemory.ToArray();
        if (IntermediateArrayFp32() != fp32Bytes || !_buffer.WrittenMemory.Span.SequenceEqual(direct))
            throw new InvalidOperationException("FP32 alternatives must produce identical RESP bytes.");
        ValidateFp32(direct);
        var valuesBytes = Values();
        ValidateValues(_buffer.WrittenMemory.Span);
        if (_buffer.Capacity != originalCapacity)
            throw new InvalidOperationException("All alternatives must fit the same preallocated output capacity.");
        Console.WriteLine("VECTOR_ENCODING_SIZE " + JsonSerializer.Serialize(new
        {
            Dimensions, Fp32Bytes = fp32Bytes, ValuesBytes = valuesBytes,
        }));
    }

    [GlobalCleanup]
    public void Cleanup() => _buffer?.Release();

    [Benchmark]
    public int DirectFp32()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        _direct.Write(ref writer);
#if RESERVED_RESP_WRITER
        writer.Complete();
#endif
        return _buffer.Count;
    }

    [Benchmark(Baseline = true)]
    public int IntermediateArrayFp32()
    {
        // Charge exactly the extra vector allocation/copy, not argument-list construction.
        byte[] bytes;
        if (BitConverter.IsLittleEndian) bytes = MemoryMarshal.AsBytes(_vector.AsSpan()).ToArray();
        else
        {
            bytes = new byte[checked(_vector.Length * sizeof(float))];
            for (var index = 0; index < _vector.Length; index++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), _vector[index]);
        }
        _intermediateArguments[1] = bytes;
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        _intermediate.Write(ref writer);
#if RESERVED_RESP_WRITER
        writer.Complete();
#endif
        return _buffer.Count;
    }

    [Benchmark]
    public int Values()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        _values.Write(ref writer);
#if RESERVED_RESP_WRITER
        writer.Complete();
#endif
        return _buffer.Count;
    }

    private void ValidateFp32(ReadOnlySpan<byte> frame)
    {
        var position = 0;
        if (RespParser.TryParseValue(frame, ref position, out var reply) != RespParseStatus.Done || position != frame.Length)
            throw new InvalidOperationException("FP32 output must be one complete RESP frame.");
        using (reply)
        {
            var arguments = reply.AsArray();
            if (arguments.Length != 5 || !arguments[2].AsSpan().SequenceEqual("FP32"u8)
                || arguments[3].AsSpan().Length != Dimensions * sizeof(float))
                throw new InvalidOperationException("Unexpected FP32 command shape.");
            var bytes = arguments[3].AsSpan();
            for (var index = 0; index < Dimensions; index++)
                if (BinaryPrimitives.ReadInt32LittleEndian(bytes[(index * sizeof(float))..]) != BitConverter.SingleToInt32Bits(_vector[index]))
                    throw new InvalidOperationException("FP32 encoding changed component bits.");
        }
    }

    private void ValidateValues(ReadOnlySpan<byte> frame)
    {
        var position = 0;
        if (RespParser.TryParseValue(frame, ref position, out var reply) != RespParseStatus.Done || position != frame.Length)
            throw new InvalidOperationException("VALUES output must be one complete RESP frame.");
        using (reply)
        {
            var arguments = reply.AsArray();
            if (arguments.Length != Dimensions + 5 || !arguments[2].AsSpan().SequenceEqual("VALUES"u8)
                || int.Parse(arguments[3].AsSpan(), CultureInfo.InvariantCulture) != Dimensions)
                throw new InvalidOperationException("Unexpected VALUES command shape.");
            for (var index = 0; index < Dimensions; index++)
            {
                var component = float.Parse(arguments[index + 4].AsSpan(), CultureInfo.InvariantCulture);
                if (BitConverter.SingleToInt32Bits(component) != BitConverter.SingleToInt32Bits(_vector[index]))
                    throw new InvalidOperationException("VALUES encoding did not round-trip a component.");
            }
        }
    }
}
