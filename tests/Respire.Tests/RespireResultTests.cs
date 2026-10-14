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
    [Arguments(false)]
    [Arguments(true)]
    public async Task Materialization_ReleasesSourceAndPreservesSerializer(bool asynchronous)
    {
        var serializer = new RecordingSerializer();
        var value = RespParser.CopyToPooled(RespDataType.BulkString, "payload"u8);
        var lease = new RespireResult(in value, serializer);
        var source = new TaskCompletionSource<RespireResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var materialized = RespireResult.MaterializeAsync(asynchronous
            ? new ValueTask<RespireResult>(source.Task)
            : ValueTask.FromResult(lease));
        if (asynchronous) source.SetResult(lease);

        var result = await materialized;

        await Assert.That(lease.IsDisposed).IsTrue();
        await Assert.That(result.As<Payload>()).IsEqualTo(new Payload("payload"));
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Materialization_ReleasesSourceWhenCopyThrows(bool asynchronous)
    {
        using var memory = new FaultingCopyMemory();
        var value = RespValue.Array(
            RespParser.CopyToPooled(RespDataType.BulkString, "first"u8),
            RespValue.BulkString(memory.Memory));
        var lease = new RespireResult(in value);
        var child = lease[0];
        var source = new TaskCompletionSource<RespireResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.Fail = true;
        var materialized = RespireResult.MaterializeAsync(asynchronous
            ? new ValueTask<RespireResult>(source.Task)
            : ValueTask.FromResult(lease));
        if (asynchronous) source.SetResult(lease);

        try
        {
            await materialized;
            throw new InvalidOperationException("Expected copy failure.");
        }
        catch (IOException error)
        {
            await Assert.That(ReferenceEquals(error, memory.Error)).IsTrue();
        }

        await Assert.That(lease.IsDisposed).IsTrue();
        await Assert.That(child.IsDisposed).IsTrue();
        await Assert.That(() => child.AsString()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task Materialization_PreservesCancellationTokenAndServerError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await RespireResult.MaterializeAsync(ValueTask.FromCanceled<RespireResult>(cancellation.Token));
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException error)
        {
            await Assert.That(error.CancellationToken).IsEqualTo(cancellation.Token);
        }

        var serverError = new RespireServerException("ERR test");
        try
        {
            await RespireResult.MaterializeAsync(ValueTask.FromException<RespireResult>(serverError));
            throw new InvalidOperationException("Expected server error.");
        }
        catch (RespireServerException error)
        {
            await Assert.That(ReferenceEquals(error, serverError)).IsTrue();
        }
    }

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

    private sealed class FaultingCopyMemory : MemoryManager<byte>
    {
        private readonly byte[] _bytes = [1];
        internal bool Fail { get; set; }
        internal IOException Error { get; } = new("Injected payload copy failure.");
        public override Span<byte> GetSpan() => Fail ? throw Error : _bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class RecordingSerializer : IRespireSerializer
    {
        public int DeserializeCalls { get; private set; }

        public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
            => Serialize(destination, value);

        public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
            => Deserialize<object>(payload);

        public void Serialize<T>(IBufferWriter<byte> destination, T value)
            => throw new NotSupportedException();

        public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        {
            DeserializeCalls++;
            return (T)(object)new Payload(System.Text.Encoding.UTF8.GetString(payload));
        }
    }
}
