using Respire.Commands;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CacheMutationDispatchReviewTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    [MatrixDataSource]
    public async Task RawBypassReadsKeepClassificationWithoutPublishingToCache(
        [Matrix(false, true)] bool interpolated, [Matrix(false, true)] bool fireAndForget,
        [Matrix(false, true)] bool prefix, [Matrix(false, true)] bool coalesce)
    {
        var gets = 0;
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                "PING" => FakeRespServer.PongReply,
                _ when command.StartsWith("GET ", StringComparison.Ordinal) => Interlocked.Increment(ref gets) == 1
                    ? "$3\r\nold\r\n"u8.ToArray() : "$3\r\nnew\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Connections = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        var bypass = client.WithoutClientCache();
        // Raw commands take physical keys; pass the view's prefix explicitly.
        RespireKey key = prefix ? "tenant:key" : "key";
        if (fireAndForget)
        {
            if (interpolated) await bypass.ExecuteFireAndForgetAsync($"GET {key}");
            else await bypass.ExecuteFireAndForgetAsync("GET", key);
            using var barrier = await bypass.ExecuteAsync("PING");
            await Assert.That(barrier.AsString()).IsEqualTo("PONG");
        }
        else
        {
            using var result = interpolated
                ? await bypass.ExecuteAsync($"GET {key}")
                : await bypass.ExecuteAsync("GET", key);
            await Assert.That(result.AsString()).IsEqualTo("new");
        }
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(Volatile.Read(ref gets)).IsEqualTo(2);
        await Assert.That(root.ClientSideCache!.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET " + (prefix ? "tenant:" : "") + "key"))
            .IsEqualTo(2);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DeferredReplicaReadOnlyHandshakeKeepsNativeAdmission(int protocol)
    {
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                "INFO SERVER" => ClusterDatabaseTests.Info(),
                "PING" => FakeRespServer.PongReply,
                _ => null,
            },
        };
        await using var owner = RespireClient.Create(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var options = owner.Core.CreateConnectionOptions(enableClientTracking: protocol == 3) with
        {
            Database = 2, RequireClusterDatabaseSupport = true, ReadOnly = true,
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, options);
        using var pong = await connection.SendAsync(new Cmd(new Verb(-1, "PING")));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        var commands = server.ReceivedCommands.ToList();
        await Assert.That(commands.IndexOf("INFO SERVER")).IsLessThan(commands.IndexOf("SELECT 2"));
        await Assert.That(commands.IndexOf("SELECT 2")).IsLessThan(commands.IndexOf("READONLY"));
        await Assert.That(commands.IndexOf("READONLY")).IsGreaterThanOrEqualTo(0);
        if (protocol == 3)
            await Assert.That(commands.IndexOf("READONLY")).IsLessThan(commands.IndexOf("CLIENT TRACKING ON OPTIN"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyWatchedValidationStillRunsExecWithCacheEnabled(bool conflict)
    {
        var execs = 0;
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                "EXEC" => Interlocked.Increment(ref execs) == 1 && conflict
                    ? "*-1\r\n"u8.ToArray() : "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Connections = 1, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var attempts = 0;
        var result = await client.RunTransactionAsync(["watched"], (_, _) => ValueTask.FromResult(++attempts),
            new RespireTransactionRetryOptions { MaxAttempts = 2 });
        var expected = conflict ? 2 : 1;
        await Assert.That(result).IsEqualTo(expected);
        await Assert.That(attempts).IsEqualTo(expected);
        await Assert.That(Volatile.Read(ref execs)).IsEqualTo(expected);
        foreach (var command in new[] { "WATCH watched", "MULTI", "EXEC" })
            await Assert.That(server.ReceivedCommands.Count(value => value == command)).IsEqualTo(expected);
    }
}
