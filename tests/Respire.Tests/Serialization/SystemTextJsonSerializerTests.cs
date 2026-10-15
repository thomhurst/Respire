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
        var serializer = new RespireSystemTextJsonSerializer(null);

        await Assert.That(serializer).IsNotNull();
    }

    [Test]
    public async Task SerializerBackedPublicApis_DeclareTrimAndAotRequirements()
    {
        MethodInfo[] methods =
        [
            typeof(IRespireSerializer).GetMethods().Single(method =>
                method.Name == nameof(IRespireSerializer.Serialize) && method.IsGenericMethod),
            typeof(IRespireSerializer).GetMethods().Single(method =>
                method.Name == nameof(IRespireSerializer.Deserialize) && method.IsGenericMethod),
            typeof(IRespireClient).GetMethod(nameof(IRespireClient.GetAsync))!,
            typeof(IRespireClient).GetMethods().Single(method =>
                method.Name == nameof(IRespireClient.SetAsync) && method.IsGenericMethod),
            typeof(RespireResult).GetMethod(nameof(RespireResult.As))!,
            .. typeof(IRespireScriptCommands).GetMethods().Where(method =>
                method.Name == nameof(IRespireScriptCommands.ExecuteAsync) && method.IsGenericMethod),
        ];

        foreach (var method in methods)
        {
            await Assert.That(method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>()).IsNotNull();
            await Assert.That(method.GetCustomAttribute<RequiresDynamicCodeAttribute>()).IsNotNull();
        }
    }

    [Test]
    public async Task ContextBackedSerializer_SupportsGenericMembers()
    {
        var serializer = RespireSystemTextJsonSerializer.FromContext(TestJsonContext.Default);
        var destination = new ArrayBufferWriter<byte>();

        serializer.Serialize(destination, new Payload("Ada", 36));
        var result = serializer.Deserialize<Payload>(destination.WrittenSpan);

        await Assert.That(result).IsEqualTo(new Payload("Ada", 36));
    }

    [Test]
    public async Task ContextBackedSerializer_SupportsTypeBasedMembers()
    {
        IRespireSerializer serializer = RespireSystemTextJsonSerializer.FromContext(TestJsonContext.Default);
        var destination = new ArrayBufferWriter<byte>();
        var value = new Payload("Ada", 36);

        serializer.Serialize(destination, typeof(Payload), value);
        var result = serializer.Deserialize(typeof(Payload), destination.WrittenSpan);

        await Assert.That(result).IsEqualTo(value);
        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).Contains("Ada");
    }

    [Test]
    public async Task SerializerContract_RequiresGenericAndTypeBasedMembers()
    {
        var methods = typeof(IRespireSerializer).GetMethods();

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods.All(method => method.IsAbstract)).IsTrue();
    }

    internal sealed record Payload(string Name, int Age);
}

[JsonSerializable(typeof(SystemTextJsonSerializerTests.Payload))]
[JsonSerializable(typeof(int))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
