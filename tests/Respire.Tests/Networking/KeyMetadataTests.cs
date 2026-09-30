using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class KeyMetadataTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImmediateMetadataPreservesNullsPrecisionAndPrefix(bool resp3Null)
    {
        var nil = Encoding.ASCII.GetBytes(resp3Null ? "_\r\n" : "$-1\r\n");
        await using var server = new FakeRespServer(
            ":1700000000123\r\n"u8.ToArray(), ":1700000000\r\n"u8.ToArray(),
            ":-2\r\n"u8.ToArray(), ":-1\r\n"u8.ToArray(),
            "$6\r\nembstr\r\n"u8.ToArray(), nil, ":42\r\n"u8.ToArray(), nil,
            ":5\r\n"u8.ToArray(), nil, ":1\r\n"u8.ToArray(), nil);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var keys = client.WithKeyPrefix("tenant:").Keys;
        await Assert.That((await keys.ExpiryTimeAsync("key")).UnixTimeMilliseconds).IsEqualTo(1700000000123);
        await Assert.That((await keys.ExpiryTimeAsync("key", ExpiryTimePrecision.Seconds)).UnixTimeMilliseconds).IsEqualTo(1700000000000);
        var missing = await keys.ExpiryTimeAsync("missing");
        var persistent = await keys.ExpiryTimeAsync("persistent");
        await Assert.That(missing.Exists).IsFalse();
        await Assert.That(persistent.Exists).IsTrue();
        await Assert.That(persistent.HasExpiry).IsFalse();
        await Assert.That(await keys.EncodingAsync("key")).IsEqualTo("embstr");
        await Assert.That(await keys.EncodingAsync("missing")).IsNull();
        await Assert.That(await keys.IdleTimeAsync("key")).IsEqualTo(TimeSpan.FromSeconds(42));
        await Assert.That(await keys.IdleTimeAsync("missing")).IsNull();
        await Assert.That(await keys.FrequencyAsync("key")).IsEqualTo(5);
        await Assert.That(await keys.FrequencyAsync("missing")).IsNull();
        await Assert.That(await keys.ReferenceCountAsync("key")).IsEqualTo(1);
        await Assert.That(await keys.ReferenceCountAsync("missing")).IsNull();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "PEXPIRETIME tenant:key", "EXPIRETIME tenant:key", "PEXPIRETIME tenant:missing", "PEXPIRETIME tenant:persistent",
            "OBJECT ENCODING tenant:key", "OBJECT ENCODING tenant:missing", "OBJECT IDLETIME tenant:key", "OBJECT IDLETIME tenant:missing",
            "OBJECT FREQ tenant:key", "OBJECT FREQ tenant:missing", "OBJECT REFCOUNT tenant:key", "OBJECT REFCOUNT tenant:missing",
        });
    }

    [Test]
    public async Task DeferredMetadataOwnsResultsAfterExecuteAndPreservesBinaryKey()
    {
        await using var server = new FakeRespServer(":1700000000\r\n"u8.ToArray(), "$6\r\nembstr\r\n"u8.ToArray(),
            ":2\r\n"u8.ToArray(), ":3\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] raw = [0xff, 0, (byte)'x'];
        RespireKey key = raw;
        using var batch = client.WithKeyPrefix("p:").CreateBatch();
        var expiry = batch.Keys.ExpiryTime(key, ExpiryTimePrecision.Seconds);
        var encoding = batch.Keys.Encoding(key);
        var idle = batch.Keys.IdleTime(key);
        var frequency = batch.Keys.Frequency(key);
        var references = batch.Keys.ReferenceCount(key);
        await batch.ExecuteAsync();
        batch.Dispose();
        await Assert.That(expiry.Result.UnixTimeMilliseconds).IsEqualTo(1700000000000);
        await Assert.That(encoding.Result).IsEqualTo("embstr");
        await Assert.That(idle.Result).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(frequency.Result).IsEqualTo(3);
        await Assert.That(references.Result).IsEqualTo(1);
        foreach (var arguments in server.ReceivedArguments)
            await Assert.That(arguments[^1].SequenceEqual(new byte[] { (byte)'p', (byte)':', 0xff, 0, (byte)'x' })).IsTrue();
    }

    [Test]
    public async Task InvalidPrecisionAndCancelledMetadataDoNotReachServer()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Keys.ExpiryTimeAsync("key", (ExpiryTimePrecision)9)).Throws<ArgumentOutOfRangeException>();
        using var batch = client.CreateBatch();
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => batch.Keys.ExpiryTime("key", (ExpiryTimePrecision)9));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        // A pre-cancelled lazy client never establishes a connection or submits a command.
        await using var lazy = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) } });
        await Assert.That(async () => await lazy.Keys.EncodingAsync("key", cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(ExpireWhen.NotExists, "NX")]
    [Arguments(ExpireWhen.Exists, "XX")]
    [Arguments(ExpireWhen.GreaterThan, "GT")]
    [Arguments(ExpireWhen.LessThan, "LT")]
    public async Task ConditionalExpiryUsesMillisecondCommands(ExpireWhen when, string token)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.Keys.ExpireAsync("key", TimeSpan.FromSeconds(1), when);
        using var batch = client.CreateBatch();
        _ = batch.Keys.Expire("key", RespireExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(1700000000123)), when);
        await batch.ExecuteAsync();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { $"PEXPIRE key 1000 {token}", $"PEXPIREAT key 1700000000123 {token}" });
    }

    [Test]
    public async Task MetadataDescriptorsRouteByTheirKeyAfterInlineSubcommands()
    {
        RespireValue key = "tenant:{route}:value";
        foreach (var descriptor in new[] { RespireCommands.Key.EXPIRETIME, RespireCommands.Key.PEXPIRETIME,
            RespireCommands.Key.OBJECT_ENCODING, RespireCommands.Key.OBJECT_IDLETIME,
            RespireCommands.Key.OBJECT_FREQ, RespireCommands.Key.OBJECT_REFCOUNT })
        {
            var command = new Cmd1(descriptor.Verb, key);
            await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
            await Assert.That(slot).IsEqualTo(ClusterHash.GetSlot("tenant:{route}:value"));
        }
    }

}
