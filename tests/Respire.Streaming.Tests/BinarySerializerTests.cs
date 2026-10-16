using System.Buffers;
using System.Runtime.CompilerServices;
using MemoryPack;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf;
using ProtoBuf.Meta;
using Respire.DependencyInjection;
using Respire.Serialization;
using Respire.Serialization.MemoryPack;
using Respire.Serialization.MessagePack;
using Respire.Serialization.Protobuf;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class BinarySerializerTests
{
    internal static IRespireSerializer Create(string format) => format switch
    {
        "MessagePack" => new RespireMessagePackSerializer(),
        "MemoryPack" => new RespireMemoryPackSerializer(),
        "Protobuf" => new RespireProtobufSerializer(),
        _ => throw new ArgumentException(format),
    };

    internal static byte[] Encode(IRespireSerializer serializer, SerializerPayload value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    [Test]
    [Arguments("MessagePack")]
    [Arguments("MemoryPack")]
    [Arguments("Protobuf")]
    public async Task GenericAndRuntimeContractsMatchNativeWireFormat(string format)
    {
        var serializer = Create(format);
        var expected = new SerializerPayload { Name = "Ada\0λ🌍", Age = 42 };
        var bytes = Encode(serializer, expected);
        var runtime = new ArrayBufferWriter<byte>();
        serializer.Serialize(runtime, typeof(SerializerPayload), expected);
        await Assert.That(runtime.WrittenSpan.SequenceEqual(bytes)).IsTrue();
        var native = format switch
        {
            "MessagePack" => MessagePackSerializer.Serialize(expected),
            "MemoryPack" => MemoryPackSerializer.Serialize(expected),
            _ => NativeProtobuf(expected),
        };
        await Assert.That(bytes.AsSpan().SequenceEqual(native)).IsTrue();
        await Verify(serializer.Deserialize<SerializerPayload>(bytes), expected);
        await Verify((SerializerPayload?)serializer.Deserialize(typeof(SerializerPayload), native), expected);

        // A prior prefix and a nonzero input offset must not be included in the message.
        var append = new ArrayBufferWriter<byte>();
        append.Write(new byte[] { 11, 22, 33 });
        serializer.Serialize(append, expected);
        await Verify(serializer.Deserialize<SerializerPayload>(append.WrittenSpan[3..]), expected);
    }

    [Test]
    [Arguments("MessagePack")]
    [Arguments("MemoryPack")]
    [Arguments("Protobuf")]
    public async Task LargePayloadsAndConcurrentUseDoNotShareMutableState(string format)
    {
        var serializer = Create(format);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            var expected = new SerializerPayload { Name = new string((char)('a' + i), 65536), Age = i };
            var bytes = Encode(serializer, expected);
            await Verify(serializer.Deserialize<SerializerPayload>(bytes), expected);
            await Verify((SerializerPayload?)serializer.Deserialize(typeof(SerializerPayload), bytes), expected);
        })));
    }

    [Test]
    [Arguments("MessagePack")]
    [Arguments("MemoryPack")]
    public async Task NullValuesRoundTrip(string format)
    {
        var serializer = Create(format);
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize<SerializerPayload?>(writer, null);
        await Assert.That(serializer.Deserialize<SerializerPayload>(writer.WrittenSpan)).IsNull();
        writer.Clear();
        serializer.Serialize(writer, typeof(SerializerPayload), null);
        await Assert.That(serializer.Deserialize(typeof(SerializerPayload), writer.WrittenSpan)).IsNull();
    }

    [Test]
    public Task MemoryPackConcurrentFirstGenericReads()
        => VerifyMemoryPackFirstUse<FirstGenericRead>(runtimeType: false, writeNull: false);

    [Test]
    public Task MemoryPackConcurrentFirstRuntimeReads()
        => VerifyMemoryPackFirstUse<FirstRuntimeRead>(runtimeType: true, writeNull: false);

    [Test]
    public Task MemoryPackConcurrentFirstGenericNullWrites()
        => VerifyMemoryPackFirstUse<FirstGenericNullWrite>(runtimeType: false, writeNull: true);

    [Test]
    public Task MemoryPackConcurrentFirstRuntimeNullWrites()
        => VerifyMemoryPackFirstUse<FirstRuntimeNullWrite>(runtimeType: true, writeNull: true);

    private static async Task VerifyMemoryPackFirstUse<TScenario>(bool runtimeType, bool writeNull)
    {
        // Each scenario uses a distinct closed model type that no other test touches.
        // Do not construct it or call the native serializer before releasing the callers.
        IRespireSerializer serializer = new RespireMemoryPackSerializer();
        using var start = new Barrier(9);
        // Race formatter lookup against independent model initialization, as happens
        // when another thread first constructs the model while an incoming read starts.
        var initialize = Task.Factory.StartNew(() =>
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Model initializer did not reach the start barrier.");
            RuntimeHelpers.RunClassConstructor(typeof(MemoryPackFirstUsePayload<TScenario>).TypeHandle);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var callers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("First-use callers did not reach the start barrier.");

            if (writeNull)
            {
                var writer = new ArrayBufferWriter<byte>();
                if (runtimeType)
                    serializer.Serialize(writer, typeof(MemoryPackFirstUsePayload<TScenario>), null);
                else
                    serializer.Serialize<MemoryPackFirstUsePayload<TScenario>?>(writer, null);

                // MemoryPack's null-object header; reading it here would warm another API.
                return writer.WrittenSpan.SequenceEqual(new byte[] { 255 });
            }

            // One Int32 member with value 42, encoded without initializing the target model.
            byte[] payload = [1, 42, 0, 0, 0];
            var value = runtimeType
                ? (MemoryPackFirstUsePayload<TScenario>?)serializer.Deserialize(typeof(MemoryPackFirstUsePayload<TScenario>), payload)
                : serializer.Deserialize<MemoryPackFirstUsePayload<TScenario>>(payload);
            return value?.Value == 42;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        // Observe and join every dedicated thread even when one of the callers fails.
        await Task.WhenAll(callers.Append(initialize));
        var results = await Task.WhenAll(callers);
        await Assert.That(results.All(success => success)).IsTrue();
    }

    public sealed class FirstGenericRead;
    public sealed class FirstRuntimeRead;
    public sealed class FirstGenericNullWrite;
    public sealed class FirstRuntimeNullWrite;

    [Test]
    public async Task ProtobufRejectsRootNullButAcceptsDefaultValuedMessages()
    {
        var serializer = new RespireProtobufSerializer();
        var writer = new ArrayBufferWriter<byte>();
        await Assert.That(() => serializer.Serialize<SerializerPayload?>(writer, null)).Throws<ArgumentNullException>();
        await Assert.That(() => serializer.Serialize(writer, typeof(SerializerPayload), null)).Throws<ArgumentNullException>();
        var expected = new SerializerPayload();
        await Verify(serializer.Deserialize<SerializerPayload>(Encode(serializer, expected)), expected);
    }

    [Test]
    [Arguments("MessagePack")]
    [Arguments("MemoryPack")]
    [Arguments("Protobuf")]
    public async Task TruncatedPayloadFailsAndSerializerCanBeReused(string format)
    {
        var serializer = Create(format);
        var expected = new SerializerPayload { Name = "truncated", Age = 500 };
        var bytes = Encode(serializer, expected);
        await Assert.That(() => serializer.Deserialize<SerializerPayload>(bytes.AsSpan(0, bytes.Length - 1))).ThrowsException();
        await Verify(serializer.Deserialize<SerializerPayload>(bytes), expected);
    }

    [Test]
    [Arguments("MessagePack")]
    [Arguments("MemoryPack")]
    [Arguments("Protobuf")]
    public async Task DependencyInjectionResolvesSingletonAndConfiguresClient(string format)
    {
        var services = new ServiceCollection();
        switch (format)
        {
            case "MessagePack": services.AddRespireMessagePackSerializer(); break;
            case "MemoryPack": services.AddRespireMemoryPackSerializer(); break;
            default: services.AddRespireProtobufSerializer(); break;
        }
        IRespireSerializer? configured = null;
        services.AddRespire(provider => RespireOptions.Parse("localhost:6379") with
        {
            Serializer = configured = provider.GetRequiredService<IRespireSerializer>(),
        });
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IRespireClient>();
        await Assert.That(ReferenceEquals(configured, provider.GetRequiredService<IRespireSerializer>())).IsTrue();
        await Verify(configured!.Deserialize<SerializerPayload>(Encode(configured, new() { Age = 9 })), new() { Age = 9 });
    }

    [Test]
    public async Task CustomOptionsAndModelsAreUsedByBothContracts()
    {
        var value = new SerializerPayload { Name = new string('λ', 4096), Age = 21 };
        var messagePackOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
        IRespireSerializer messagePack = new RespireMessagePackSerializer(messagePackOptions);
        await Verify(MessagePackSerializer.Deserialize<SerializerPayload>(Encode(messagePack, value), messagePackOptions), value);
        await Verify((SerializerPayload?)messagePack.Deserialize(typeof(SerializerPayload), MessagePackSerializer.Serialize(value, messagePackOptions)), value);

        var memoryPackOptions = MemoryPackSerializerOptions.Utf16;
        IRespireSerializer memoryPack = new RespireMemoryPackSerializer(memoryPackOptions);
        await Verify(MemoryPackSerializer.Deserialize<SerializerPayload>(Encode(memoryPack, value), memoryPackOptions), value);
        var writer = new ArrayBufferWriter<byte>();
        memoryPack.Serialize(writer, typeof(SerializerPayload), value);
        await Verify(MemoryPackSerializer.Deserialize<SerializerPayload>(writer.WrittenSpan, memoryPackOptions), value);

        var model = RuntimeTypeModel.Create();
        model.Add(typeof(CustomProtobufPayload), false).Add(7, nameof(CustomProtobufPayload.Name));
        IRespireSerializer protobuf = new RespireProtobufSerializer(model);
        writer.Clear();
        protobuf.Serialize(writer, typeof(CustomProtobufPayload), new CustomProtobufPayload { Name = "custom model" });
        await Assert.That(protobuf.Deserialize<CustomProtobufPayload>(writer.WrittenSpan)!.Name).IsEqualTo("custom model");
        writer.Clear();
        protobuf.Serialize(writer, new CustomProtobufPayload { Name = "generic" });
        await Assert.That(((CustomProtobufPayload)protobuf.Deserialize(typeof(CustomProtobufPayload), writer.WrittenSpan)!).Name).IsEqualTo("generic");
    }

    [Test]
    public async Task ProtobufRuntimeTypeUsesTheDeclaredBaseContract()
    {
        IRespireSerializer serializer = new RespireProtobufSerializer();
        ProtobufBase value = new ProtobufDerived { Name = "base", Extra = 42 };
        var generic = new ArrayBufferWriter<byte>();
        var runtime = new ArrayBufferWriter<byte>();
        serializer.Serialize(generic, value);
        serializer.Serialize(runtime, typeof(ProtobufBase), value);
        await Assert.That(runtime.WrittenSpan.SequenceEqual(generic.WrittenSpan)).IsTrue();
        var restored = (ProtobufDerived)serializer.Deserialize(typeof(ProtobufBase), runtime.WrittenSpan)!;
        await Assert.That(restored.Name).IsEqualTo("base");
        await Assert.That(restored.Extra).IsEqualTo(42);
    }

    public class CustomProtobufPayload { public string Name { get; set; } = ""; }

    [ProtoContract, ProtoInclude(10, typeof(ProtobufDerived))]
    public class ProtobufBase { [ProtoMember(1)] public string Name { get; set; } = ""; }

    [ProtoContract]
    public class ProtobufDerived : ProtobufBase { [ProtoMember(1)] public int Extra { get; set; } }

    internal static async Task Verify(SerializerPayload? actual, SerializerPayload expected)
    {
        await Assert.That(actual).IsNotNull();
        await Assert.That(actual!.Name).IsEqualTo(expected.Name);
        await Assert.That(actual.Age).IsEqualTo(expected.Age);
    }

    private static byte[] NativeProtobuf(SerializerPayload value)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, value);
        return stream.ToArray();
    }
}

[MessagePackObject, MemoryPackable, ProtoContract]
public partial class SerializerPayload
{
    [Key(0), ProtoMember(1)] public string Name { get; set; } = "";
    [Key(1), ProtoMember(2)] public int Age { get; set; }
}

[MemoryPackable]
public partial class MemoryPackFirstUsePayload<TScenario>
{
    public int Value { get; set; }
}
