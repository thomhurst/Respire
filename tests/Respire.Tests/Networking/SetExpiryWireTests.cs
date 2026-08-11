using System.Buffers;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// Wire-format tests for the <see cref="RespireExpiry"/> expiry union: relative goes out as PX,
/// absolute as PXAT, keep as KEEPTTL, none as no option at all — in every SET-style surface.
/// </summary>
public class SetExpiryWireTests
{
    private static readonly DateTimeOffset Instant = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);

    [Test]
    public async Task Set_WithoutExpiry_SendsNoExpiryOption()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("key", "value");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key value");
    }

    [Test]
    public async Task Set_WithRelativeExpiry_SendsPx()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("key", "value", TimeSpan.FromSeconds(30));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key value PX 30000");
    }

    [Test]
    public async Task Set_WithAbsoluteExpiry_SendsPxAt()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("key", "value", RespireExpiry.At(Instant));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key value PXAT 1700000000123");
    }

    [Test]
    public async Task Set_WithKeep_SendsKeepTtl()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("key", "value", RespireExpiry.Keep);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key value KEEPTTL");
    }

    [Test]
    [Arguments(SetWhen.NotExists, "NX")]
    [Arguments(SetWhen.Exists, "XX")]
    public async Task Set_WithConditionAndExpiry_SendsBothOptions(SetWhen when, string token)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("key", "value", TimeSpan.FromSeconds(1), when);
        await client.SetAsync("key", "value", RespireExpiry.Keep, when);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo($"SET key value PX 1000 {token}");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo($"SET key value {token} KEEPTTL");
    }

    [Test]
    public async Task SetTyped_WithAbsoluteExpiry_SendsPxAt()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("key", 42, Instant);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key 42 PXAT 1700000000123");
    }

    [Test]
    public async Task Set_WithPersist_ThrowsBeforeConnectingOrSerializing()
    {
        var serializer = new ThrowingSerializer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", 1) },
            ConnectTimeout = TimeSpan.FromMilliseconds(50),
            Serializer = serializer,
        });

        await Assert.That(async () => await client.SetAsync("raw", (RespireValue)"value", RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.SetAsync("typed", new Payload(1), RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.Strings.GetAndSetAsync(
                "raw-get", (RespireValue)"value", RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.Strings.GetAndSetAsync(
                "typed-get", new Payload(2), RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();

        using var batch = client.CreateBatch();
        await Assert.That(() => batch.Set("batch-raw", (RespireValue)"value", RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Set("batch-typed", new Payload(3), RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Strings.GetAndSet(
                "batch-get-raw", (RespireValue)"value", RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Strings.GetAndSet(
                "batch-get-typed", new Payload(4), RespireExpiry.Persist))
            .ThrowsExactly<ArgumentException>();

        await Assert.That(serializer.SerializeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task GetAndSet_WithOptionsAndGenericValue_ReturnsPreviousValues()
    {
        await using var server = new FakeRespServer(
            "$3\r\nold\r\n"u8.ToArray(),
            "$2\r\n42\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var previous = await client.Strings.GetAndSetAsync(
            "key", "new", TimeSpan.FromSeconds(5), SetWhen.Exists);
        var typedPrevious = await client.Strings.GetAndSetAsync(
            "number", 43, RespireExpiry.Keep, SetWhen.NotExists);

        await Assert.That(previous).IsEqualTo("old");
        await Assert.That(typedPrevious).IsEqualTo(42);
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key new PX 5000 XX GET");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET number 43 NX KEEPTTL GET");
    }

    [Test]
    public async Task BatchGetAndSet_WithOptionsAndGenericValue_MirrorsClient()
    {
        await using var server = new FakeRespServer(
            "$3\r\nold\r\n"u8.ToArray(),
            "$2\r\n42\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var previous = batch.Strings.GetAndSet(
            "key", "new", TimeSpan.FromSeconds(5), SetWhen.Exists);
        var typedPrevious = batch.Strings.GetAndSet(
            "number", 43, RespireExpiry.Keep, SetWhen.NotExists);
        await batch.ExecuteAsync();

        await Assert.That(previous.Result).IsEqualTo("old");
        await Assert.That(typedPrevious.Result).IsEqualTo(42);
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key new PX 5000 XX GET");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET number 43 NX KEEPTTL GET");
    }

    [Test]
    public async Task Batch_CarriesTheExpiryUnion()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var relative = batch.Set("key", "value", TimeSpan.FromSeconds(5));
        var keep = batch.Set("other", "value", RespireExpiry.Keep);
        await batch.ExecuteAsync();

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET key value PX 5000");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET other value KEEPTTL");
        await Assert.That(await relative).IsTrue();
        await Assert.That(await keep).IsTrue();
    }

    [Test]
    public async Task Transaction_CarriesTheExpiryUnion()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n+OK\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var transaction = client.CreateTransaction();
        var pending = transaction.Set("key", "value", RespireExpiry.At(Instant));
        await transaction.CommitAsync();

        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET key value PXAT 1700000000123");
        await Assert.That(pending.Result).IsTrue();
    }

    [Test]
    public async Task SetMany_WithoutExpiry_SendsMSet()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.Strings.SetManyAsync(("a", "1"), ("b", "2"));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("MSET a 1 b 2");
    }

    [Test]
    public async Task SetMany_WithExpiryUnion_SendsMSetEx()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.Strings.SetManyExpireAsync(TimeSpan.FromSeconds(3), ("a", "1"));
        await client.Strings.SetManyExpireAsync(RespireExpiry.At(Instant), SetWhen.Exists, ("b", "2"));
        await client.Strings.SetManyExpireAsync(RespireExpiry.Keep, SetWhen.NotExists, ("c", "3"));
        await client.Strings.SetManyExpireAsync(RespireExpiry.None, SetWhen.NotExists, ("d", "4"));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("MSETEX 1 a 1 PX 3000");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("MSETEX 1 b 2 XX PXAT 1700000000123");
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("MSETEX 1 c 3 NX KEEPTTL");
        await Assert.That(server.ReceivedCommands[3]).IsEqualTo("MSETEX 1 d 4 NX");
    }

    private sealed record Payload(int Value);

    private sealed class ThrowingSerializer : IRespireSerializer
    {
        public int SerializeCalls { get; private set; }

        public void Serialize<T>(IBufferWriter<byte> destination, T value)
        {
            SerializeCalls++;
            throw new InvalidOperationException("Serializer must not run for an invalid SET expiry.");
        }

        public T? Deserialize<T>(ReadOnlySpan<byte> payload) => default;
    }
}
