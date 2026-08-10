using System.Buffers;
using System.Text;
using Respire.Protocol;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class PrimitiveCodecTests
{
    [Test]
    public async Task NumericPrimitives_BypassSerializer_OnWrite()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);

        await Assert.That(client.Serialize((byte)255).ToString()).IsEqualTo("255");
        await Assert.That(client.Serialize((sbyte)-128).ToString()).IsEqualTo("-128");
        await Assert.That(client.Serialize((short)-32_768).ToString()).IsEqualTo("-32768");
        await Assert.That(client.Serialize((ushort)65_535).ToString()).IsEqualTo("65535");
        await Assert.That(client.Serialize(-2_147_483_648).ToString()).IsEqualTo("-2147483648");
        await Assert.That(client.Serialize(4_294_967_295U).ToString()).IsEqualTo("4294967295");
        await Assert.That(client.Serialize(long.MinValue).ToString()).IsEqualTo("-9223372036854775808");
        await Assert.That(client.Serialize(ulong.MaxValue).ToString()).IsEqualTo("18446744073709551615");
        await Assert.That(client.Serialize(3.5F).ToString()).IsEqualTo("3.5");
        await Assert.That(client.Serialize(3.5D).ToString()).IsEqualTo("3.5");
        await Assert.That(client.Serialize(3.5M).ToString()).IsEqualTo("3.5");
        await Assert.That(serializer.SerializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task NumericPrimitives_BypassSerializer_OnRead()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);

        await Assert.That(client.DeserializeBorrowed<byte>(Bulk("255"))).IsEqualTo((byte)255);
        await Assert.That(client.DeserializeBorrowed<sbyte>(Bulk("-128"))).IsEqualTo((sbyte)-128);
        await Assert.That(client.DeserializeBorrowed<short>(Bulk("-32768"))).IsEqualTo((short)-32_768);
        await Assert.That(client.DeserializeBorrowed<ushort>(Bulk("65535"))).IsEqualTo((ushort)65_535);
        await Assert.That(client.DeserializeBorrowed<int>(Bulk("-2147483648"))).IsEqualTo(-2_147_483_648);
        await Assert.That(client.DeserializeBorrowed<uint>(Bulk("4294967295"))).IsEqualTo(4_294_967_295U);
        await Assert.That(client.DeserializeBorrowed<long>(Bulk("-9223372036854775808"))).IsEqualTo(long.MinValue);
        await Assert.That(client.DeserializeBorrowed<ulong>(Bulk("18446744073709551615"))).IsEqualTo(ulong.MaxValue);
        await Assert.That(client.DeserializeBorrowed<float>(Bulk("3.5"))).IsEqualTo(3.5F);
        await Assert.That(client.DeserializeBorrowed<double>(Bulk("3.5"))).IsEqualTo(3.5D);
        await Assert.That(client.DeserializeBorrowed<decimal>(Bulk("3.5"))).IsEqualTo(3.5M);
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ReadOnlyMemory_UsesConfiguredSerializer_OnWriteAndRead()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);
        ReadOnlyMemory<byte> payload = new byte[] { 0, 1, 254, 255 };

        _ = client.Serialize(payload);
        _ = client.DeserializeBorrowed<ReadOnlyMemory<byte>>(Bulk("{}"));

        await Assert.That(serializer.SerializeCalls).IsEqualTo(1);
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RawCompatibility_PreservesMemoryAndNonFiniteScalars()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);
        ReadOnlyMemory<byte> memory = new byte[] { 0, 1, 254, 255 };

        _ = client.SerializeRawCompatible(memory);
        await Assert.That(client.SerializeRawCompatible('A').ToString()).IsEqualTo("65");
        await Assert.That(client.SerializeRawCompatible(float.NaN).ToString()).IsEqualTo("NaN");
        await Assert.That(client.SerializeRawCompatible(float.PositiveInfinity).ToString()).IsEqualTo("Infinity");
        await Assert.That(client.SerializeRawCompatible(float.NegativeInfinity).ToString()).IsEqualTo("-Infinity");
        await Assert.That(client.SerializeRawCompatible(double.NaN).ToString()).IsEqualTo("NaN");
        await Assert.That(client.SerializeRawCompatible(double.PositiveInfinity).ToString()).IsEqualTo("Infinity");
        await Assert.That(client.SerializeRawCompatible(double.NegativeInfinity).ToString()).IsEqualTo("-Infinity");
        await Assert.That(serializer.SerializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task RawCompatibility_UsesSerializerForErasedTypes()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);
        object memory = (ReadOnlyMemory<byte>)new byte[] { 0, 1, 254, 255 };
        object character = 'A';
        object single = float.NaN;
        object number = double.PositiveInfinity;

        _ = client.SerializeRawCompatible(memory);
        _ = client.SerializeRawCompatible(character);
        _ = client.SerializeRawCompatible(single);
        _ = client.SerializeRawCompatible(number);

        await Assert.That(serializer.SerializeCalls).IsEqualTo(4);
    }

    [Test]
    public async Task FloatingPointReads_RejectNonFiniteValues()
    {
        await using var client = CreateClient(new CountingSerializer());

        foreach (var payload in new[] { "NaN", "Infinity", "-Infinity", "1e400" })
        {
            await Assert.That(() => client.DeserializeBorrowed<double>(Bulk(payload))).Throws<FormatException>();
        }

        foreach (var payload in new[] { "NaN", "Infinity", "-Infinity", "1e100" })
        {
            await Assert.That(() => client.DeserializeBorrowed<float>(Bulk(payload))).Throws<FormatException>();
        }
    }

    [Test]
    public async Task FloatingPointReads_RejectGroupSeparators()
    {
        await using var client = CreateClient(new CountingSerializer());

        await Assert.That(() => client.DeserializeBorrowed<float>(Bulk("1,234"))).Throws<FormatException>();
        await Assert.That(() => client.DeserializeBorrowed<double>(Bulk("1,234"))).Throws<FormatException>();
        await Assert.That(() => client.DeserializeBorrowed<decimal>(Bulk("1,234"))).Throws<FormatException>();
    }

    [Test]
    public async Task Boolean_FastPath_PreservesJsonWrites_AndAcceptsRedisValues()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);

        await Assert.That(client.Serialize(true).ToString()).IsEqualTo("true");
        await Assert.That(client.Serialize(false).ToString()).IsEqualTo("false");
        await Assert.That(client.DeserializeBorrowed<bool>(Bulk("true"))).IsTrue();
        await Assert.That(client.DeserializeBorrowed<bool>(Bulk("false"))).IsFalse();
        await Assert.That(client.DeserializeBorrowed<bool>(Bulk("1"))).IsTrue();
        await Assert.That(client.DeserializeBorrowed<bool>(Bulk("0"))).IsFalse();
        await Assert.That(serializer.SerializeCalls).IsEqualTo(0);
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(" true ", true)]
    [Arguments("false\n", false)]
    [Arguments("\t1\r", true)]
    [Arguments("\r\n0\t", false)]
    public async Task BooleanReads_AcceptJsonWhitespace(string payload, bool expected)
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);
        var message = new RespireMessage("flags", pattern: null, Encoding.UTF8.GetBytes(payload), serializer);

        await Assert.That(client.DeserializeBorrowed<bool>(Bulk(payload))).IsEqualTo(expected);
        await Assert.That(message.As<bool>()).IsEqualTo(expected);
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task BooleanReads_RejectNonJsonWhitespace()
    {
        await using var client = CreateClient(new CountingSerializer());

        await Assert.That(() => client.DeserializeBorrowed<bool>(Bulk("\vtrue\v"))).Throws<FormatException>();
    }

    [Test]
    public async Task NullablePrimitives_UseFastPath()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);
        int? written = 42;

        var serialized = client.Serialize(written);
        var deserialized = client.DeserializeBorrowed<int?>(Bulk("42"));

        await Assert.That(serialized.ToString()).IsEqualTo("42");
        await Assert.That(deserialized).IsEqualTo(42);
        await Assert.That(serializer.SerializeCalls).IsEqualTo(0);
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task NullablePrimitives_PreserveEncodedNull()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);
        var message = new RespireMessage("values", pattern: null, "\tnull\r\n"u8.ToArray(), serializer);

        var deserialized = client.DeserializeBorrowed<int?>(Bulk(" null "));
        var messageValue = message.As<bool?>();

        await Assert.That(deserialized).IsNull();
        await Assert.That(messageValue).IsNull();
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task NonPrimitiveValues_StillUseConfiguredSerializer()
    {
        var serializer = new CountingSerializer();
        await using var client = CreateClient(serializer);

        _ = client.Serialize(new Payload(42));
        _ = client.DeserializeBorrowed<Payload>(Bulk("{}"));
        _ = client.Serialize(TestValue.One);
        _ = client.DeserializeBorrowed<TestValue>(Bulk("1"));

        await Assert.That(serializer.SerializeCalls).IsEqualTo(2);
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(2);
    }

    [Test]
    public async Task PubSubPrimitives_BypassSerializer()
    {
        var serializer = new CountingSerializer();
        var message = new RespireMessage("numbers", pattern: null, "42"u8.ToArray(), serializer);

        var value = message.As<int>();

        await Assert.That(value).IsEqualTo(42);
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task RespireValue_AcceptsEveryNumericPrimitiveWithoutAmbiguity()
    {
        RespireValue @byte = (byte)255;
        RespireValue sbyteValue = (sbyte)-128;
        RespireValue int16 = (short)-32_768;
        RespireValue uint16 = (ushort)65_535;
        RespireValue uint32 = uint.MaxValue;
        RespireValue uint64 = ulong.MaxValue;
        RespireValue single = 3.5F;
        RespireValue @decimal = 3.5M;

        await Assert.That(@byte.ToString()).IsEqualTo("255");
        await Assert.That(sbyteValue.ToString()).IsEqualTo("-128");
        await Assert.That(int16.ToString()).IsEqualTo("-32768");
        await Assert.That(uint16.ToString()).IsEqualTo("65535");
        await Assert.That(uint32.ToString()).IsEqualTo("4294967295");
        await Assert.That(uint64.ToString()).IsEqualTo("18446744073709551615");
        await Assert.That(single.ToString()).IsEqualTo("3.5");
        await Assert.That(@decimal.ToString()).IsEqualTo("3.5");
    }

    private static RespireClient CreateClient(IRespireSerializer serializer)
        => RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("localhost") },
            Serializer = serializer,
        });

    private static RespValue Bulk(string value) => RespValue.BulkString(value);

    private sealed record Payload(int Value);

    private enum TestValue
    {
        One = 1,
    }

    private sealed class CountingSerializer : IRespireSerializer
    {
        public int SerializeCalls { get; private set; }
        public int DeserializeCalls { get; private set; }

        public void Serialize<T>(IBufferWriter<byte> destination, T value)
        {
            SerializeCalls++;
            destination.Write("{}"u8);
        }

        public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        {
            DeserializeCalls++;
            return default;
        }
    }
}
