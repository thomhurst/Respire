using System.Buffers;
using Respire.Protocol;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireResultTests
{
    [Test]
    public async Task AggregateResult_SupportsForeachAndLinq()
    {
        using var result = new RespireResult(RespValue.Array(
            RespValue.Integer(1), RespValue.Integer(2), RespValue.Integer(3)));

        var values = result.Select(static item => item.AsInteger()).ToArray();

        await Assert.That(values).IsEquivalentTo(new long[] { 1, 2, 3 });
    }

    [Test]
    public async Task As_UsesConfiguredSerializer()
    {
        var serializer = new RecordingSerializer();
        using var result = new RespireResult(RespValue.BulkString("payload"), serializer);

        var value = result.As<Payload>();

        await Assert.That(value).IsEqualTo(new Payload("payload"));
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task As_ConvertsNativeRespPrimitives()
    {
        using var integer = new RespireResult(RespValue.Integer(42));
        using var boolean = new RespireResult(RespValue.Boolean(true));
        using var @double = new RespireResult(RespValue.Double(3.5));

        await Assert.That(integer.As<long>()).IsEqualTo(42);
        await Assert.That(integer.As<int?>()).IsEqualTo(42);
        await Assert.That(boolean.As<bool>()).IsTrue();
        await Assert.That(@double.As<double>()).IsEqualTo(3.5);
    }

    [Test]
    public async Task As_RejectsNonFiniteNativeRespDoubles()
    {
        using var nan = new RespireResult(RespValue.Double(double.NaN));
        using var infinity = new RespireResult(RespValue.Double(double.PositiveInfinity));
        using var floatOverflow = new RespireResult(RespValue.Double(1e100));

        await Assert.That(() => nan.As<double>()).ThrowsExactly<FormatException>();
        await Assert.That(() => infinity.As<double>()).ThrowsExactly<FormatException>();
        await Assert.That(() => floatOverflow.As<float>()).ThrowsExactly<FormatException>();
    }

    [Test]
    public async Task RootAndNestedViews_ExposeDisposedLifetime()
    {
        var result = new RespireResult(RespValue.Array(RespValue.BulkString("value")));
        var nested = result[0];

        result.Dispose();

        await Assert.That(result.IsDisposed).IsTrue();
        await Assert.That(nested.IsDisposed).IsTrue();
        await Assert.That(() => result.Count).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => nested.AsString()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task AsDouble_ReturnsRespDoublesUnchanged()
    {
        using var result = new RespireResult(RespValue.Double(3.5));

        await Assert.That(result.AsDouble()).IsEqualTo(3.5);
    }

    [Test]
    [Arguments("3.14", 3.14)]
    [Arguments("-0.5", -0.5)]
    [Arguments("42", 42d)]
    public async Task AsDouble_ParsesNumericTextWithInvariantCulture(string reply, double expected)
    {
        using var result = new RespireResult(RespValue.BulkString(reply));

        await Assert.That(result.AsDouble()).IsEqualTo(expected);
    }

    [Test]
    public async Task AsDouble_ParsesIntegerReplies()
    {
        using var result = new RespireResult(RespValue.Integer(7));

        await Assert.That(result.AsDouble()).IsEqualTo(7d);
    }

    [Test]
    [Arguments("not-a-number")]
    [Arguments("1,5")]
    [Arguments("")]
    public async Task AsDouble_ThrowsWhenTheReplyIsNotANumber(string reply)
    {
        using var result = new RespireResult(RespValue.BulkString(reply));

        await Assert.That(() => result.AsDouble()).Throws<FormatException>();
    }

    [Test]
    public async Task AsDouble_ThrowsForNullReplies()
    {
        using var result = new RespireResult(RespValue.Null);

        await Assert.That(() => result.AsDouble()).Throws<FormatException>();
    }

    private sealed record Payload(string Value);

    private sealed class RecordingSerializer : IRespireSerializer
    {
        public int DeserializeCalls { get; private set; }

        public void Serialize<T>(IBufferWriter<byte> destination, T value)
            => throw new NotSupportedException();

        public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        {
            DeserializeCalls++;
            return (T)(object)new Payload(System.Text.Encoding.UTF8.GetString(payload));
        }
    }
}
