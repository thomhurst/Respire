using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class TypedSerializationLifetimeTests
{
    [Test]
    public async Task TypedNullGuardPreservesReferenceAndNullableValueRejection()
    {
        await using var client = RespireClient.Create("localhost");
        await Assert.That(() => client.Serialize<string?>(null)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => client.Serialize<int?>(null)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => client.Serialize<TextPayload?>(null)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task InvalidDestinationDoesNotFreezeSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        var serializer = new SystemTextJsonSerializer(options);
        await Assert.That(() => serializer.Serialize<int>(null!, 42)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(options.IsReadOnly).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GenericMetadataCacheKeepsSerializerOptionsSeparate(bool generated)
    {
        var lowerOptions = new JsonSerializerOptions { PropertyNamingPolicy = generated ? JsonNamingPolicy.CamelCase : null };
        var upperOptions = new JsonSerializerOptions();
        var lower = generated ? SystemTextJsonSerializer.FromContext(new TestJsonContext(lowerOptions)) : new SystemTextJsonSerializer(lowerOptions);
        var upper = generated ? SystemTextJsonSerializer.FromContext(new TestJsonContext(upperOptions)) : new SystemTextJsonSerializer(upperOptions);
        // Reflection-backed options remain mutable until first use, as before.
        if (!generated) lowerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        var destination = new ArrayBufferWriter<byte>();
        var payload = new SystemTextJsonSerializerTests.Payload("Ada", 36);
        for (var index = 0; index < 3; index++)
        {
            destination.Clear();
            lower.Serialize(destination, payload);
            await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).Contains("\"name\"");
            await Assert.That(lower.Deserialize<SystemTextJsonSerializerTests.Payload>(destination.WrittenSpan)).IsEqualTo(payload);
            destination.Clear();
            upper.Serialize(destination, payload);
            await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).Contains("\"Name\"");
            await Assert.That(upper.Deserialize<SystemTextJsonSerializerTests.Payload>(destination.WrittenSpan)).IsEqualTo(payload);
        }
        await Assert.That(() => lowerOptions.WriteIndented = true).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task SerializedArgumentsRemainOwnedAcrossLaterCalls()
    {
        await using var client = RespireClient.Create("localhost");
        var first = client.Serialize(new TextPayload("first"));
        var expected = first.ToString();
        for (var index = 0; index < 32; index++)
            _ = client.Serialize(new TextPayload(new string('x', 4096)));
        await Assert.That(first.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task NestedSerializationAndConverterFailureDoNotCorruptReusableWriters()
    {
        var converter = new NestedConverter();
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost", 6379)], Serializer = new SystemTextJsonSerializer(options),
        });
        converter.Nested = () => client.Serialize(new TypedSerializationAllocationTests.Payload(7)).ToString();
        for (var index = 0; index < 3; index++)
        {
            await Assert.That(() => client.Serialize(new Outer(-1))).ThrowsExactly<InvalidOperationException>();
            var result = client.Serialize(new Outer(42));
            await Assert.That(result.ToString()).IsEqualTo("{\"Value\":42,\"Nested\":{\"Value\":7}}");
        }
    }

    private sealed record TextPayload(string Text);
    private sealed record Outer(int Value);

    private sealed class NestedConverter : JsonConverter<Outer>
    {
        internal Func<string> Nested = null!;
        public override Outer? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Outer value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("Value", value.Value);
            if (value.Value < 0) throw new InvalidOperationException("Converter failed after writing a prefix.");
            writer.WritePropertyName("Nested");
            writer.WriteRawValue(Nested());
            writer.WriteEndObject();
        }
    }
}
