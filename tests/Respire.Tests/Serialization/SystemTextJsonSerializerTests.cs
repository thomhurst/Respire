using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task WriterOptionsMatchNativeSerialization(bool useContext, bool useRuntimeType)
    {
        var options = new JsonSerializerOptions();
        var reflectionSerializer = new RespireSystemTextJsonSerializer(options);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.WriteIndented = true;
        options.IndentCharacter = '\t';
        options.IndentSize = 1;
        options.NewLine = "\n";

        var context = useContext ? new TestJsonContext(options) : null;
        var configured = context is null ? reflectionSerializer : RespireSystemTextJsonSerializer.FromContext(context);
        var defaults = new RespireSystemTextJsonSerializer();
        var payload = new Payload("Café <tea> ☕", 36);
        var destination = new ArrayBufferWriter<byte>();

        var defaultBefore = Serialize(defaults);
        var custom = Serialize(configured);
        var defaultAfter = Serialize(defaults);

        string expected = context is null ? JsonSerializer.Serialize(payload, options) : JsonSerializer.Serialize(payload, context.Payload);
        await Assert.That(custom).IsEqualTo(expected);
        await Assert.That(defaultBefore).IsEqualTo(JsonSerializer.Serialize(payload));
        await Assert.That(defaultAfter).IsEqualTo(defaultBefore);

        string Serialize(RespireSystemTextJsonSerializer serializer)
        {
            destination.Clear();

            if (useRuntimeType)
            {
                serializer.Serialize(destination, typeof(Payload), payload);
            }
            else
            {
                serializer.Serialize(destination, payload);
            }

            return Encoding.UTF8.GetString(destination.WrittenSpan);
        }
    }

    [Test]
    [Arguments(0, 64, false)]
    [Arguments(0, 64, true)]
    [Arguments(2, 2, false)]
    [Arguments(2, 2, true)]
    public async Task WriterDepthLimitMatchesNativeSerialization(int maxDepth, int depthLimit, bool useContext)
    {
        var options = new JsonSerializerOptions { MaxDepth = maxDepth };
        options.Converters.Add(new NestedArraysConverter());

        var context = useContext ? new TestJsonContext(options) : null;
        var serializer = context is null
            ? new RespireSystemTextJsonSerializer(options)
            : RespireSystemTextJsonSerializer.FromContext(context);
        var destination = new ArrayBufferWriter<byte>();
        var atLimit = new NestedArrays(depthLimit);
        var aboveLimit = new NestedArrays(depthLimit + 1);

        string expected = SerializeNative(atLimit);
        serializer.Serialize(destination, atLimit);
        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).IsEqualTo(expected);

        destination.Clear();
        serializer.Serialize(destination, typeof(NestedArrays), atLimit);
        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).IsEqualTo(expected);

        destination.Clear();
        await Assert.That(() => SerializeNative(aboveLimit)).ThrowsExactly<JsonException>();
        await Assert.That(() => serializer.Serialize(destination, aboveLimit)).ThrowsExactly<JsonException>();

        destination.Clear();
        await Assert.That(() => serializer.Serialize(destination, typeof(NestedArrays), aboveLimit))
            .ThrowsExactly<JsonException>();

        string SerializeNative(NestedArrays value)
        {
            return context is null
                ? JsonSerializer.Serialize(value, options)
                : JsonSerializer.Serialize(value, context.NestedArrays);
        }
    }

    [Test]
    public async Task SerializerContract_RequiresGenericAndTypeBasedMembers()
    {
        var methods = typeof(IRespireSerializer).GetMethods();

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods.All(method => method.IsAbstract)).IsTrue();
    }

    internal sealed record Payload(string Name, int Age);

    internal readonly record struct NestedArrays(int Depth);

    private sealed class NestedArraysConverter : JsonConverter<NestedArrays>
    {
        public override NestedArrays Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, NestedArrays value, JsonSerializerOptions options)
        {
            // Write containers directly so the test exercises the writer's depth check.
            for (int depth = 0; depth < value.Depth; depth++)
            {
                writer.WriteStartArray();
            }

            for (int depth = 0; depth < value.Depth; depth++)
            {
                writer.WriteEndArray();
            }
        }
    }
}

[JsonSerializable(typeof(SystemTextJsonSerializerTests.NestedArrays))]
[JsonSerializable(typeof(SystemTextJsonSerializerTests.Payload))]
[JsonSerializable(typeof(int))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
