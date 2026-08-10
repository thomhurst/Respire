using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class SystemTextJsonSerializerTests
{
    [Test]
    public async Task ExplicitNullOptionsRemainSourceCompatible()
    {
        var serializer = new SystemTextJsonSerializer(null);

        await Assert.That(serializer).IsNotNull();
    }

    [Test]
    public async Task SerializerBackedPublicApis_DeclareTrimAndAotRequirements()
    {
        var methods = new[]
        {
            typeof(IRespireSerializer).GetMethods().Single(method =>
                method.Name == nameof(IRespireSerializer.Serialize) && method.IsGenericMethod),
            typeof(IRespireSerializer).GetMethods().Single(method =>
                method.Name == nameof(IRespireSerializer.Deserialize) && method.IsGenericMethod),
            typeof(IRespireClient).GetMethod(nameof(IRespireClient.GetAsync))!,
            typeof(IRespireClient).GetMethods().Single(method =>
                method.Name == nameof(IRespireClient.SetAsync) && method.IsGenericMethod),
            typeof(RespireResult).GetMethod(nameof(RespireResult.As))!,
            typeof(IScriptCommands).GetMethods().Single(method =>
                method.Name == nameof(IScriptCommands.ExecuteAsync) && method.IsGenericMethod),
        };

        foreach (var method in methods)
        {
            await Assert.That(method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>()).IsNotNull();
            await Assert.That(method.GetCustomAttribute<RequiresDynamicCodeAttribute>()).IsNotNull();
        }
    }

    [Test]
    public async Task ContextBackedSerializer_SupportsGenericMembers()
    {
        var serializer = SystemTextJsonSerializer.FromContext(TestJsonContext.Default);
        var destination = new ArrayBufferWriter<byte>();

        serializer.Serialize(destination, new Payload("Ada", 36));
        var result = serializer.Deserialize<Payload>(destination.WrittenSpan);

        await Assert.That(result).IsEqualTo(new Payload("Ada", 36));
    }

    [Test]
    public async Task ContextBackedSerializer_SupportsTypeBasedMembers()
    {
        IRespireSerializer serializer = SystemTextJsonSerializer.FromContext(TestJsonContext.Default);
        var destination = new ArrayBufferWriter<byte>();
        var value = new Payload("Ada", 36);

        serializer.Serialize(destination, typeof(Payload), value);
        var result = serializer.Deserialize(typeof(Payload), destination.WrittenSpan);

        await Assert.That(result).IsEqualTo(value);
        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).Contains("Ada");
    }

    [Test]
    public async Task LegacySerializer_TypeBasedMembers_ExplainUnsupportedOperation()
    {
        IRespireSerializer serializer = new LegacySerializer();
        var destination = new ArrayBufferWriter<byte>();

        var serialize = () => serializer.Serialize(destination, typeof(Payload), new Payload("Ada", 36));
        var deserialize = () => serializer.Deserialize(typeof(Payload), "{}"u8);

        await Assert.That(serialize).Throws<NotSupportedException>();
        await Assert.That(deserialize).Throws<NotSupportedException>();
    }

    private sealed class LegacySerializer : IRespireSerializer
    {
        public void Serialize<T>(IBufferWriter<byte> destination, T value)
        {
        }

        public T? Deserialize<T>(ReadOnlySpan<byte> payload) => default;
    }

    internal sealed record Payload(string Name, int Age);
}

[JsonSerializable(typeof(SystemTextJsonSerializerTests.Payload))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
