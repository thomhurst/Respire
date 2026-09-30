using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelRoutingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();

    [Test]
    public async Task LazyClientDiscoversOnFirstOperationAndPrefixViewsShareThePrimary()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await using var prefixed = client.WithKeyPrefix("tenant:");
        await Assert.That(sentinel.CommandsSeen).IsEqualTo(0);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        await Assert.That(client.IsConnected).IsFalse();
        await prefixed.SetAsync("key", "value").AsTask().WaitAsync(Limit);
        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", primary.Port));
        await Assert.That(prefixed.Endpoint).IsEqualTo(client.Endpoint);
        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "SET tenant:key value"]);
    }

    [Test]
    public async Task ReadOnlyRetiresStalePrimaryAndNextWriteUsesTheSameClientCore()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && rejectWrites
            ? "-READONLY You can't write against a read only replica.\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await using var prefixed = client.WithKeyPrefix("tenant:");
        var core = client.Core;
        Volatile.Write(ref primaryPort, promoted.Port);
        rejectWrites = true;
        await Assert.That(async () => await prefixed.SetAsync("rejected", "value").AsTask().WaitAsync(Limit))
            .Throws<RespireServerException>();
        await prefixed.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(client.Core).IsSameReferenceAs(core);
        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", promoted.Port));
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET tenant:next value"]);
        await Assert.That(oldPrimary.ReceivedCommands.Count(command => command.StartsWith("SET "))).IsEqualTo(1);
    }

    [Test]
    public async Task DisconnectReResolvesWithoutReplayingAnAcceptedWrite()
    {
        await using var oldPrimary = Primary();
        oldPrimary.CloseConnectionAfterCommand = 2;
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        Volatile.Write(ref primaryPort, promoted.Port);
        await Assert.That(async () => await client.IncrementAsync("ambiguous").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).IsEquivalentTo(["ROLE", "INCR ambiguous"]);
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    private static RespireOptions Options(int sentinelPort) => new()
    {
        Endpoints = [new("127.0.0.1", sentinelPort)], SentinelPrimaryName = "mymaster",
        Connections = 1, ConnectTimeout = Limit, CommandTimeout = Limit,
    };

    private static FakeRespServer Primary(Func<int, string, byte[]?>? reply = null)
        => new(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (connection, command) => reply?.Invoke(connection, command)
                ?? (command == "ROLE" ? PrimaryRole : FakeRespServer.OkReply),
        };

    private static FakeRespServer Sentinel(Func<int> primaryPort)
        => new(8, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? AddressReply(primaryPort()) : "*0\r\n"u8.ToArray(),
        };

    private static byte[] AddressReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");
}
