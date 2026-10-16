using System.Text;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class TransactionClusterTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ImportingConnectionIsReusedOnlyWhenExecClearsTransaction(bool watched, bool queueAbort)
    {
        await using var source = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(16, FakeRespServer.OkReply);
        Configure(source, target);
        var setup = source.ReplyOverride!;
        source.ReplyOverride = (id, command) => command switch
        {
            "GET key" => Ask(target),
            "EXEC" => "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray(),
            _ => setup(id, command),
        };
        target.ReplyOverride = (_, command) => command switch
        {
            "GET key" => Encoding.ASCII.GetBytes(queueAbort ? "-ERR rejected command\r\n" : "+QUEUED\r\n"),
            "EXEC" => Encoding.ASCII.GetBytes(queueAbort
                ? "-EXECABORT Transaction discarded because of previous errors.\r\n"
                : "-NOPERM EXEC denied\r\n"),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(source));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var transaction = await client.CreateCompatibilityTransactionAsync(watched ? ["key"] : [], default);
            var pending = transaction.GetString("key");
            var error = await Assert.ThrowsAsync<RespireServerException>(async () => await transaction.CommitAsync());
            Assert.Equal(queueAbort ? "ERR" : "NOPERM", error.Code);
            Assert.Throws<RespireServerException>(() => pending.Result);
        }
        var commands = target.ReceivedCommands;
        var connections = target.ReceivedConnectionIds;
        var execConnections = commands.Select((command, index) => (command, index))
            .Where(item => item.command == "EXEC").Select(item => connections[item.index]).ToArray();
        Assert.Equal(2, execConnections.Length);
        Assert.Equal(queueAbort ? 1 : 2, execConnections.Distinct().Count());
    }

    [Test]
    public async Task UnconditionalReadTransactionFollowsOnlyACompleteRejection()
    {
        await using var source = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(16, FakeRespServer.OkReply);
        Configure(source, target);
        var setup = source.ReplyOverride!;
        source.ReplyOverride = (id, command) => command switch
        {
            "HGET key row" or "HGET key version" => Ask(target),
            "EXEC" => "-EXECABORT Transaction discarded\r\n"u8.ToArray(),
            _ => setup(id, command),
        };
        target.ReplyOverride = (_, command) => command switch
        {
            "HGET key row" or "HGET key version" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => "*2\r\n$4\r\ndata\r\n$1\r\n1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await using var adapter = RespireConnectionMultiplexer.Wrap(client);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var transaction = adapter.GetDatabase().CreateTransaction();
            var row = transaction.HashGetAsync("key", "row");
            var version = transaction.HashGetAsync("key", "version");
            Assert.True(await transaction.ExecuteAsync());
            Assert.Equal("data", (string?)await row);
            Assert.Equal("1", (string?)await version);
        }
        var commands = target.ReceivedCommands;
        var start = commands.ToList().IndexOf("ASKING");
        Assert.Equal(Enumerable.Range(0, 2).SelectMany(_ => new[] { "ASKING", "MULTI", "HGET key row", "HGET key version", "EXEC" }), commands.Skip(start));
        Assert.Single(target.ReceivedConnectionIds.Skip(start).Distinct());
        Assert.DoesNotContain(target.ReceivedCommands, command => command.StartsWith("WATCH "));
    }

    [Test]
    public async Task UnconditionalPartialExecResultIsNotReplayed()
    {
        await using var source = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(16, FakeRespServer.OkReply);
        Configure(source, target);
        var setup = source.ReplyOverride!;
        source.ReplyOverride = (id, command) => command switch
        {
            "HSET key first value" or "HGET key second" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => [.. "*2\r\n:1\r\n"u8.ToArray(), .. Ask(target)],
            _ => setup(id, command),
        };
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await using var adapter = RespireConnectionMultiplexer.Wrap(client);
        var transaction = adapter.GetDatabase().CreateTransaction();
        var first = transaction.HashSetAsync("key", "first", "value");
        var second = transaction.HashGetAsync("key", "second");
        Assert.True(await transaction.ExecuteAsync());
        Assert.True(await first);
        await Assert.ThrowsAsync<RespireServerException>(() => second);
        Assert.Single(source.ReceivedCommands.Where(command => command == "EXEC"));
        Assert.Empty(target.ReceivedCommands);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImportingWatchAndCommitUseTheSameSocket(bool conflict)
    {
        await using var source = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(16, FakeRespServer.OkReply);
        Configure(source, target);
        target.ReplyOverride = (_, command) => command switch
        {
            "SET key value" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => Encoding.ASCII.GetBytes(conflict ? "*-1\r\n" : "*1\r\n+OK\r\n"),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(source));
        var originalOwner = client.Core.Cluster!.RoutingSnapshot[ClusterHash.GetSlot("key")].Primary;
        await using var transaction = await client.CreateCompatibilityTransactionAsync(["key"], default);
        var pending = transaction.Strings.Set("key", "value");
        Assert.Equal(!conflict, await transaction.CommitAsync());
        if (conflict) Assert.Throws<RespireTransactionAbortedException>(() => pending.Result);
        else Assert.True(pending.Result);
        var commands = target.ReceivedCommands;
        var start = commands.ToList().IndexOf("ASKING");
        Assert.Equal(new[] { "ASKING", "WATCH key", "ASKING", "MULTI", "SET key value", "EXEC" }, commands.Skip(start));
        Assert.Single(target.ReceivedConnectionIds.Skip(start).Distinct());
        Assert.DoesNotContain("MULTI", source.ReceivedCommands);
        Assert.Same(originalOwner, client.Core.Cluster.RoutingSnapshot[ClusterHash.GetSlot("key")].Primary);
    }

    [Test]
    public async Task RedirectLoopsAreBoundedAndNativeWatchStillRejectsAsk()
    {
        await using var source = new FakeRespServer(32, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(32, FakeRespServer.OkReply);
        Configure(source, target);
        target.ReplyOverride = (_, command) => command.StartsWith("WATCH ") ? Ask(target) : null;
        await using var client = await RespireClient.ConnectAsync(Options(source));
        var error = await Assert.ThrowsAsync<RespireTransactionRetryException>(async () =>
            await client.CreateCompatibilityTransactionAsync(["key"], default));
        Assert.Equal("ASK", error.ServerError.Code);
        Assert.Equal(ClusterRouter.RedirectLimit, target.ReceivedCommands.Count(command => command == "WATCH key"));
        await Assert.ThrowsAsync<RespireTransactionRetryException>(async () => await client.CreateTransactionAsync(["key"]));
        Assert.Equal(ClusterRouter.RedirectLimit, target.ReceivedCommands.Count(command => command == "WATCH key"));
        Assert.DoesNotContain("MULTI", target.ReceivedCommands);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImportingWatchHonorsCancellationAndTimeout(bool cancel)
    {
        await using var source = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(16, FakeRespServer.OkReply);
        Configure(source, target);
        using var cancellation = new CancellationTokenSource();
        target.SuppressReply = command =>
        {
            if (command != "WATCH key") return false;
            if (cancel) cancellation.Cancel();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            CommandTimeout = TimeSpan.FromSeconds(2),
        });
        if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await client.CreateCompatibilityTransactionAsync(["key"], cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
            await Assert.ThrowsAsync<RespireTimeoutException>(async () =>
                await client.CreateCompatibilityTransactionAsync(["key"], default));
        Assert.DoesNotContain("MULTI", target.ReceivedCommands);
        Assert.Single(target.ReceivedCommands.Where(command => command == "WATCH key"));
    }

    [Test]
    public async Task AskDeniedBeforeMultiSubmitsNoWrites()
    {
        await using var source = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(16, FakeRespServer.OkReply);
        Configure(source, target);
        var asking = 0;
        target.ReplyOverride = (_, command) => command == "ASKING" && Interlocked.Increment(ref asking) == 2
            ? "-NOPERM ASKING denied\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await using var transaction = await client.CreateCompatibilityTransactionAsync(["key"], default);
        var pending = transaction.Strings.Set("key", "value");
        await Assert.ThrowsAsync<RespireServerException>(async () => await transaction.CommitAsync());
        Assert.Throws<RespireServerException>(() => pending.Result);
        Assert.DoesNotContain("SET key value", target.ReceivedCommands);
        Assert.DoesNotContain("EXEC", target.ReceivedCommands);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CommitRedirectOrLostReplyIsNotReplayed(bool loseReply)
    {
        await using var source = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(16, FakeRespServer.OkReply);
        Configure(source, target);
        target.ReplyOverride = (_, command) => command switch
        {
            "SET key value" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => Ask(target),
            _ => null,
        };
        target.SuppressReply = command => loseReply && command == "EXEC";
        await using var client = await RespireClient.ConnectAsync(Options(source) with { CommandTimeout = TimeSpan.FromSeconds(2) });
        await using var transaction = await client.CreateCompatibilityTransactionAsync(["key"], default);
        var pending = transaction.Strings.Set("key", "value");
        if (loseReply)
            await Assert.ThrowsAsync<RespireTimeoutException>(async () => await transaction.CommitAsync());
        else
            await Assert.ThrowsAsync<RespireTransactionRetryException>(async () => await transaction.CommitAsync());
        Assert.Equal(RespirePendingStatus.Faulted, pending.Status);
        Assert.Single(target.ReceivedCommands.Where(command => command == "WATCH key"));
        Assert.Single(target.ReceivedCommands.Where(command => command == "SET key value"));
        Assert.Single(target.ReceivedCommands.Where(command => command == "EXEC"));
    }

    private static RespireOptions Options(FakeRespServer source) => new()
    {
        Protocol = RespireProtocol.Resp2, UseCluster = true, Connections = 1,
        ClusterTopologyRefreshInterval = null, CommandTimeout = TimeSpan.FromSeconds(10),
        Endpoints = [new("127.0.0.1", source.Port)],
    };

    private static byte[] Ask(FakeRespServer target)
        => Encoding.ASCII.GetBytes($"-ASK {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n");

    private static void Configure(FakeRespServer source, FakeRespServer target)
    {
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n$7\r\nprimary\r\n");
        source.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            "WATCH key" => Ask(target),
            _ => null,
        };
    }
}
