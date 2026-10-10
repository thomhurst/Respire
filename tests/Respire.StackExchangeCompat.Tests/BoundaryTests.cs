using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using Respire.Protocol;
using Respire.Tests.Networking;
using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class BoundaryTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ValuesRetainStackExchangeWireBytes(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        byte[] bytes = [0xff, 0, 0x80, 13, 10, 0xfe];
        var first = new SequenceSegment(bytes.AsMemory(0, 2));
        var last = first.Append(bytes.AsMemory(2));
        RedisValue[] values = [
            bytes, bytes.AsMemory(1, 4), new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length),
            "", "Grüße 🫁", long.MinValue, ulong.MaxValue, 1.2345678901234567, double.PositiveInfinity, true,
        ];
        foreach (var value in values)
        {
            var expected = (byte[]?)value;
            var converted = await client.ExecuteStackExchangeAsync("ECHO", [value]);
            await Assert.That(((byte[]?)converted)!.SequenceEqual(expected!)).IsTrue();
        }
        await Assert.That(client.IsConnected).IsTrue();
        await client.PingAsync();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BinaryAndPrefixedKeysRemainNativeKeys(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        RedisKey key = ((RedisKey)new byte[] { 0xff, 0, 0x81 }).Append(new byte[] { 0, 0xfe });
        RedisValue value = new byte[] { 0x80, 0, 0xff };
        var nativeKey = key.ToRespireKey();
        await client.Strings.SetAsync(nativeKey, value.ToRespireValue());
        using var stored = await client.ExecuteAsync("GET", (byte[]?)key);
        await Assert.That(stored.AsBytes().SequenceEqual((byte[]?)value!)).IsTrue();

        var view = client.WithKeyPrefix("interop:");
        await Assert.That(async () => await view.ExecuteStackExchangeAsync(
            RespireCommands.String.SET, ["key", value])).Throws<NotSupportedException>();
        await view.Strings.SetAsync(((RedisKey)"key").ToRespireKey(), value.ToRespireValue());
        using var prefixed = await client.ExecuteAsync("GET", "interop:key");
        await Assert.That(prefixed.AsBytes().SequenceEqual((byte[]?)value!)).IsTrue();
        await Assert.That(await client.GetStringAsync("key")).IsNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task NullEmptyAndMissingStayDistinct(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        await Assert.That(RedisValue.Null.ToRespireValue().IsNull).IsTrue();
        await Assert.That(RedisValue.EmptyString.ToRespireValue().IsNull).IsFalse();
        await Assert.That(() => default(RedisKey).ToRespireKey()).Throws<ArgumentNullException>();
        var emptyKey = ((RedisKey)Array.Empty<byte>()).ToRespireKey();
        await client.Strings.SetAsync(emptyKey, RedisValue.EmptyString.ToRespireValue());
        var empty = await client.ExecuteStackExchangeAsync("GET", [RedisValue.EmptyString]);
        var missing = await client.ExecuteStackExchangeAsync("GET", [$"missing:{Guid.NewGuid():N}"]);
        await Assert.That(empty.IsNull).IsFalse();
        await Assert.That(((byte[]?)empty)!.Length).IsEqualTo(0);
        await Assert.That(missing.IsNull).IsTrue();
        await Assert.That(missing.Resp2Type).IsEqualTo(ResultType.BulkString);
        await Assert.That(async () => await client.ExecuteStackExchangeAsync("ECHO", [RedisValue.Null]))
            .Throws<ArgumentException>();
        await client.PingAsync();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task NumericConversionIgnoresCurrentCulture(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            RedisValue value = 1234.5678901234567;
            var result = await client.ExecuteStackExchangeAsync("ECHO", [value]);
            await Assert.That(((byte[]?)result)!.SequenceEqual((byte[]?)value!)).IsTrue();
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BridgePreservesNativeCancellationAndServerErrors(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null,
            SuppressReply = command =>
            {
                if (command != "ECHO value") return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var cancellationClient = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var cancellation = new CancellationTokenSource();
        var pending = cancellationClient.ExecuteStackExchangeAsync("ECHO", ["value"], cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        OperationCanceledException? failure = null;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException error) { failure = error; }
        await Assert.That(failure is not null).IsTrue();
        await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
        await client.ExecuteStackExchangeAsync("INCR", ["not-an-integer-key"]);
        await client.ExecuteStackExchangeAsync("SET", ["not-an-integer-key", "text"]);
        await Assert.That(async () => await client.ExecuteStackExchangeAsync("INCR", ["not-an-integer-key"]))
            .Throws<RespireServerException>();
        await client.PingAsync();
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol)
        => RespireClient.ConnectAsync($"redis://{fixture.Host}:{fixture.Port}/{fixture.Database}?protocol={protocol}");

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RealRedisScalarsAndAggregatesKeepProtocolShapes(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        byte[] binary = [0xff, 0, 0x80, 0xfe];
        var ok = await client.ExecuteStackExchangeAsync("SET", ["integer", long.MaxValue - 1]);
        var integer = await client.ExecuteStackExchangeAsync("INCR", ["integer"]);
        await Assert.That(ok.Resp3Type).IsEqualTo(ResultType.SimpleString);
        await Assert.That(integer.Resp3Type).IsEqualTo(ResultType.Integer);
        await Assert.That((long)integer).IsEqualTo(long.MaxValue);

        await client.ExecuteStackExchangeAsync("HSET", ["hash", "field", binary]);
        var map = await client.ExecuteStackExchangeAsync("HGETALL", ["hash"]);
        await Assert.That(map.Resp3Type).IsEqualTo(protocol == 3 ? ResultType.Map : ResultType.Array);
        await Assert.That((string?)map[0]).IsEqualTo("field");
        await Assert.That(((byte[]?)map[1])!.SequenceEqual(binary)).IsTrue();

        await client.ExecuteStackExchangeAsync("SADD", ["set", binary]);
        var set = await client.ExecuteStackExchangeAsync("SMEMBERS", ["set"]);
        await Assert.That(set.Resp3Type).IsEqualTo(protocol == 3 ? ResultType.Set : ResultType.Array);
        await Assert.That(((byte[]?)set[0])!.SequenceEqual(binary)).IsTrue();

        var nested = await client.ExecuteStackExchangeAsync("EVAL",
            ["return {ARGV[1], {1, false, ''}}", 0, binary]);
        await Assert.That(nested.Resp3Type).IsEqualTo(ResultType.Array);
        await Assert.That(((byte[]?)nested[0])!.SequenceEqual(binary)).IsTrue();
        await Assert.That((long)nested[1][0]).IsEqualTo(1L);
        await Assert.That(nested[1][1].IsNull).IsTrue();
        await Assert.That(nested[1][2].IsNull).IsFalse();

        var nullArray = await client.ExecuteStackExchangeAsync("BLPOP", ["missing-list", "0.01"]);
        await Assert.That(nullArray.IsNull).IsTrue();
        await Assert.That(nullArray.Resp3Type).IsEqualTo(ResultType.Null);
        await Assert.That(nullArray.Resp2Type).IsEqualTo(protocol == 2 ? ResultType.Array : ResultType.BulkString);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RealRedisResp3ScalarsFollowNegotiatedProtocol(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var result = await client.ExecuteStackExchangeAsync("EVAL",
            ["redis.setresp(3); return {true, false, {double=1.25}, {big_number='123456789012345678901234567890'}}", 0]);
        await Assert.That(result[0].Resp3Type).IsEqualTo(protocol == 3 ? ResultType.Boolean : ResultType.Integer);
        await Assert.That((bool)result[0]).IsTrue();
        await Assert.That(result[1].Resp3Type).IsEqualTo(protocol == 3 ? ResultType.Boolean : ResultType.Integer);
        await Assert.That((bool)result[1]).IsFalse();
        await Assert.That(result[2].Resp3Type).IsEqualTo(protocol == 3 ? ResultType.Double : ResultType.BulkString);
        await Assert.That((double)result[2]).IsEqualTo(1.25);
        await Assert.That(result[3].Resp3Type).IsEqualTo(protocol == 3 ? ResultType.BigInteger : ResultType.BulkString);
        await Assert.That((string?)result[3]).IsEqualTo("123456789012345678901234567890");
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}

public class ResultBoundaryTests
{
    [Test]
    public async Task NullIdentitySurvivesFragmentedParsingAndOwnedCopies()
    {
        var frame = "*3\r\n$-1\r\n*-1\r\n_\r\n"u8.ToArray();
        using var parser = new RespParseState(int.MaxValue);
        var position = 0;
        RespValue parsed = default;
        for (var length = 1; length <= frame.Length; length++)
        {
            var status = parser.TryParse(frame.AsSpan(0, length), ref position, out parsed, out _);
            await Assert.That(status).IsEqualTo(length == frame.Length
                ? RespParseStatus.Done : RespParseStatus.NeedMoreData);
        }
        using (parsed)
        using (var root = RespireResult.CreateOwned(in parsed))
        {
            await Assert.That(root[0].NullWireType).IsEqualTo(RespDataType.BulkString);
            await Assert.That(root[1].NullWireType).IsEqualTo(RespDataType.Array);
            await Assert.That(root[2].NullWireType).IsEqualTo(RespDataType.Null);
            var result = root.ToStackExchangeResult();
            await Assert.That(result[0].Resp2Type).IsEqualTo(ResultType.BulkString);
            await Assert.That(result[1].Resp2Type).IsEqualTo(ResultType.Array);
            await Assert.That(result[2].Resp3Type).IsEqualTo(ResultType.Null);
            for (var index = 0; index < 3; index++)
            {
                var value = parsed.AsArray()[index];
                await Assert.That(value.Equals(RespValue.Null)).IsTrue();
                await Assert.That(value.GetHashCode()).IsEqualTo(RespValue.Null.GetHashCode());
            }
        }
    }

    [Test]
    [Arguments("+OK\r\n", ResultType.SimpleString, "OK")]
    [Arguments(":9223372036854775807\r\n", ResultType.Integer, "9223372036854775807")]
    [Arguments(":-9223372036854775808\r\n", ResultType.Integer, "-9223372036854775808")]
    [Arguments(",1.2345678901234567\r\n", ResultType.Double, "1.2345678901234567")]
    [Arguments(",inf\r\n", ResultType.Double, "+inf")]
    [Arguments("#t\r\n", ResultType.Boolean, "1")]
    [Arguments("#f\r\n", ResultType.Boolean, "0")]
    [Arguments("(123456789012345678901234567890\r\n", ResultType.BigInteger, "123456789012345678901234567890")]
    public async Task ScalarShapeAndValueSurviveRootDisposal(string frame, ResultType expectedType, string expectedValue)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(frame));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var root = await client.ExecuteAsync("ECHO", "ignored");
        RedisResult converted;
        try
        {
            converted = root.ToStackExchangeResult();
            await Assert.That(root.IsDisposed).IsFalse();
        }
        finally { root.Dispose(); }
        await Assert.That(converted.Resp3Type).IsEqualTo(expectedType);
        await Assert.That((string?)converted).IsEqualTo(expectedValue);
    }

    [Test]
    [Arguments("$-1\r\n", ResultType.BulkString)]
    [Arguments("*-1\r\n", ResultType.Array)]
    [Arguments("_\r\n", ResultType.BulkString)]
    public async Task NullShapesPreserveResp2Identity(string frame, ResultType expectedResp2Type)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(frame));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var result = await client.ExecuteStackExchangeAsync("ECHO", ["ignored"]);
        await Assert.That(result.IsNull).IsTrue();
        await Assert.That(result.Resp3Type).IsEqualTo(ResultType.Null);
        await Assert.That(result.Resp2Type).IsEqualTo(expectedResp2Type);
    }

    [Test]
    [Arguments("*0\r\n", ResultType.Array)]
    [Arguments("%0\r\n", ResultType.Map)]
    [Arguments("~0\r\n", ResultType.Set)]
    public async Task EmptyAggregatesKeepTheirShape(string frame, ResultType expectedType)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(frame));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var result = await client.ExecuteStackExchangeAsync("ECHO", ["ignored"]);
        await Assert.That(result.IsNull).IsFalse();
        await Assert.That(result.Resp3Type).IsEqualTo(expectedType);
        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task NestedAggregatesAndBinaryPayloadRemainStandalone()
    {
        byte[] frame = [.. "*2\r\n%1\r\n+k\r\n~2\r\n:1\r\n_\r\n$4\r\n"u8, 0xff, 0, 0x80, 0xfe, 13, 10];
        await using var server = new FakeRespServer(frame);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var root = await client.ExecuteAsync("ECHO", "ignored");
        var converted = root.ToStackExchangeResult();
        root.Dispose();
        await Assert.That(converted.Resp3Type).IsEqualTo(ResultType.Array);
        await Assert.That(converted[0].Resp3Type).IsEqualTo(ResultType.Map);
        await Assert.That(converted[0].Length).IsEqualTo(2);
        await Assert.That(converted[0][1].Resp3Type).IsEqualTo(ResultType.Set);
        await Assert.That((long)converted[0][1][0]).IsEqualTo(1L);
        await Assert.That(converted[0][1][1].IsNull).IsTrue();
        await Assert.That(((byte[]?)converted[1])!.SequenceEqual(new byte[] { 0xff, 0, 0x80, 0xfe })).IsTrue();
    }

    [Test]
    [Arguments("$4\r\nabcd\r\n", false)]
    [Arguments("*1\r\n-ERR nested\r\n", true)]
    [Arguments("=7\r\ntxt:abc\r\n", true)]
    public async Task BridgeDisposesRootOnSuccessAndConversionFailure(string frame, bool unsupported)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(frame))
        {
            ReplyOverride = (_, command) => command == "PING" ? FakeRespServer.PongReply : null,
        };
        await using var native = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = DispatchProxy.Create<IRespireClient, CapturingClient>();
        var capture = (CapturingClient)client;
        capture.Native = native;
        using var cancellation = new CancellationTokenSource();
        try
        {
            if (unsupported)
                await Assert.That(async () => await client.ExecuteStackExchangeAsync("ECHO", ["value"],
                    RespireCommandFlags.NoRedirect, cancellation.Token)).Throws<NotSupportedException>();
            else
                await Assert.That((string?)await client.ExecuteStackExchangeAsync("ECHO", ["value"],
                    RespireCommandFlags.NoRedirect, cancellation.Token)).IsEqualTo("abcd");
            await Assert.That(capture.Captured.IsDisposed).IsTrue();
            await Assert.That(capture.Flags).IsEqualTo(RespireCommandFlags.NoRedirect);
            await Assert.That(capture.Token).IsEqualTo(cancellation.Token);
            await native.PingAsync();
        }
        finally { capture.Captured.Dispose(); }
    }

    [Test]
    public async Task DisposedSourceCannotBeConverted()
    {
        await using var server = new FakeRespServer("$1\r\nx\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var root = await client.ExecuteAsync("ECHO", "ignored");
        root.Dispose();
        await Assert.That(() => root.ToStackExchangeResult()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task BinaryAndOwnershipControlsDetectLossyOrBorrowedConversion()
    {
        byte[] payload = [0xff, 0, 0x80, 0xfe];
        var lossy = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload));
        await Assert.That(lossy.SequenceEqual(payload)).IsFalse();
        var value = RespValue.BulkString(payload);
        var root = new RespireResult(in value);
        var borrowed = root.AsSpan().ToArray();
        var converted = root.ToStackExchangeResult();
        payload.AsSpan().Fill(42);
        await Assert.That(root.AsSpan().SequenceEqual(borrowed)).IsFalse();
        root.Dispose();
        await Assert.That(((byte[]?)converted)!.SequenceEqual(borrowed)).IsTrue();
        await Assert.That(() => root.AsBytes()).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments("*1\r\n-ERR nested\r\n")]
    [Arguments("*1\r\n!10\r\nERR nested\r\n")]
    [Arguments("=7\r\ntxt:abc\r\n")]
    public async Task UnsupportedShapesGiveNativeApiGuidance(string frame)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(frame));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var root = await client.ExecuteAsync("ECHO", "ignored");
        NotSupportedException? failure = null;
        try { root.ToStackExchangeResult(); }
        catch (NotSupportedException error) { failure = error; }
        await Assert.That(failure is not null).IsTrue();
        await Assert.That(failure!.Message.Contains("native ExecuteAsync and RespireResult", StringComparison.Ordinal)).IsTrue();
        await Assert.That(root.IsDisposed).IsFalse();
    }

    public class CapturingClient : DispatchProxy
    {
        public IRespireClient Native = null!;
        public RespireResult Captured;
        public RespireCommandFlags Flags;
        public CancellationToken Token;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name != nameof(IRespireClient.ExecuteAsync) || arguments?.Length != 4)
                throw new NotSupportedException("The ownership control permits only the raw execution boundary.");
            Flags = (RespireCommandFlags)arguments[2]!;
            Token = (CancellationToken)arguments[3]!;
            return CaptureAsync((RespireCommand)arguments[0]!, (RespireValue[])arguments[1]!);
        }

        private async ValueTask<RespireResult> CaptureAsync(RespireCommand command, RespireValue[] arguments)
        {
            Captured = await Native.ExecuteAsync(command, arguments, Flags, Token);
            return Captured;
        }
    }
}
