using System.Text;
using System.Net.Sockets;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterMultiKeyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MGetPreservesOrderDuplicatesNullsAndPrefix(bool cache)
    {
        await using var fixture = await Fixture.CreateAsync(cache: cache);
        var client = fixture.Client.WithKeyPrefix("prefix:");
        var keys = new RespireKey[] { "{a}one", "{b}two", "{a}nil", "{b}two" };
        var expected = new string?[] { "prefix:{a}one", "prefix:{b}two", null, "prefix:{b}two" };
        await Assert.That((await client.Strings.GetManyAsync(keys)).SequenceEqual(expected)).IsTrue();
        await Assert.That((await client.Strings.GetManyAsync<string>(keys)).SequenceEqual(expected)).IsTrue();
        await Assert.That(fixture.Low.ReceivedCommands.Contains("MGET prefix:{b}two prefix:{b}two")).IsTrue();
        await Assert.That(fixture.High.ReceivedCommands.Contains("MGET prefix:{a}one prefix:{a}nil")).IsTrue();
        if (cache)
        {
            await Assert.That(fixture.Low.ReceivedCommands.Count(c => c.StartsWith("MGET ", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(fixture.High.ReceivedCommands.Count(c => c.StartsWith("MGET ", StringComparison.Ordinal))).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("MGET")]
    [Arguments("MSET")]
    [Arguments("DEL")]
    [Arguments("UNLINK")]
    [Arguments("EXISTS")]
    [Arguments("TOUCH")]
    public async Task RawCommandsSplitAndMerge(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        RespireValue[] args = operation == "MSET" ? ["{a}one", "v1", "{b}two", "v2", "{a}one", "v3"]
            : ["{a}one", "{b}two", "{a}one"];
        using var result = await fixture.Client.ExecuteAsync(operation, args);
        if (operation == "MGET")
            await Assert.That(Enumerable.Range(0, result.Count).Select(index => result[index].AsString())
                .SequenceEqual(new[] { "{a}one", "{b}two", "{a}one" })).IsTrue();
        else if (operation == "MSET") await Assert.That(result.AsString()).IsEqualTo("OK");
        else await Assert.That(result.AsInteger()).IsEqualTo(3);
        await Assert.That(fixture.Low.ReceivedCommands.Contains(operation == "MSET" ? "MSET {b}two v2" : $"{operation} {{b}}two")).IsTrue();
        await Assert.That(fixture.High.ReceivedCommands.Contains(operation == "MSET" ? "MSET {a}one v1 {a}one v3" : $"{operation} {{a}}one {{a}}one")).IsTrue();
    }

    [Test]
    public async Task FacetWritesAndCatalogExistsUseTheSplitter()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.That(await fixture.Client.Strings.SetManyAsync(("{a}one", "1"), ("{b}two", "2"))).IsTrue();
        await Assert.That(await fixture.Client.Keys.DeleteAsync("{a}one", "{b}two")).IsEqualTo(2);
        await Assert.That(await fixture.Client.Keys.UnlinkAsync("{a}one", "{b}two")).IsEqualTo(2);
        await Assert.That(await fixture.Client.Keys.TouchAsync("{a}one", "{b}two")).IsEqualTo(2);
        using var result = await fixture.Client.ExecuteAsync(RespireCommands.Key.EXISTS, "{a}one", "{b}two");
        await Assert.That(result.AsInteger()).IsEqualTo(2);
    }

    [Test]
    public async Task GroupsRunConcurrentlyAndOneHookOwnsCompletion()
    {
        var hook = new RecordingHook();
        await using var fixture = await Fixture.CreateAsync(hook: hook);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        void Received() { if (Interlocked.Increment(ref count) == 2) both.TrySetResult(); }
        fixture.Low.GateCommand("MGET ", both.Task, Received);
        fixture.High.GateCommand("MGET ", both.Task, Received);
        await fixture.Client.Strings.GetManyAsync("{a}one", "{b}two").AsTask().WaitAsync(Limit);
        await Assert.That(hook.Entered).IsEqualTo(1);
        await Assert.That(hook.Completed).IsEqualTo(1);
        await Assert.That(hook.Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    public async Task PartialFailureNamesFailedNodeAndJoinsSuccessfulGroup()
    {
        var hook = new RecordingHook();
        await using var fixture = await Fixture.CreateAsync(hook: hook);
        var successReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Low.GateCommand("MSET ", Task.CompletedTask, () => successReceived.TrySetResult());
        fixture.High.GateCommand("MSET ", successReceived.Task);
        fixture.High.ReplyOverride = (_, command) => command.StartsWith("MSET ", StringComparison.Ordinal)
            ? "-ERR rejected\r\n"u8.ToArray() : fixture.Reply(command);
        var error = await Assert.That(async () => await fixture.Client.Strings.SetManyAsync(("{a}one", "1"), ("{b}two", "2")))
            .ThrowsExactly<RespireClusterPartitionException>();
        await Assert.That(error!.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", fixture.High.Port));
        await Assert.That(error.Slot).IsEqualTo(ClusterHash.GetSlot("{a}one"));
        await Assert.That(error.Message.Contains(fixture.High.Port.ToString(), StringComparison.Ordinal)).IsTrue();
        await Assert.That(error.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(hook.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(fixture.Low.ReceivedCommands.Count(c => c == "MSET {b}two 2")).IsEqualTo(1);
    }

    [Test]
    public async Task SameSlotRemainsOneCommandAndUnsafeCommandsStillReject()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Client.Strings.GetManyAsync("{a}one", "{a}two");
        await Assert.That(fixture.High.ReceivedCommands.Count(c => c.StartsWith("MGET ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(fixture.Low.ReceivedCommands.Any(c => c.StartsWith("MGET ", StringComparison.Ordinal))).IsFalse();
        foreach (var operation in new[] { "MSETNX", "SINTER", "EVAL" })
        {
            RespireValue[] args = operation switch
            {
                "MSETNX" => ["{a}one", "1", "{b}two", "2"],
                "EVAL" => ["return 1", 2, "{a}one", "{b}two"],
                _ => ["{a}one", "{b}two"],
            };
            await Assert.That(async () => { using var reply = await fixture.Client.ExecuteAsync(operation, args); })
                .ThrowsExactly<RespireCrossSlotException>();
        }
        await using var transaction = fixture.Client.CreateTransaction();
        await Assert.That(() => transaction.Strings.GetMany("{a}one", "{b}two")).Throws<Exception>();
    }

    [Test]
    public async Task CancellationStaysCancellationAndDoesNotReplaySuccessfulWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.High.GateCommand("MSET ", release.Task, () => received.TrySetResult());
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Client.Strings.SetManyAsync([("{a}one", "1"), ("{b}two", "2")], cancellation.Token).AsTask();
        await received.Task.WaitAsync(Limit);
        cancellation.Cancel();
        try { await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>(); }
        finally { release.TrySetResult(); }
        await Assert.That(fixture.Low.ReceivedCommands.Count(c => c == "MSET {b}two 2")).IsLessThanOrEqualTo(1);
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        internal int Entered;
        internal int Completed;
        internal RespireCommandOutcome Outcome;
        public override object? OnExecuting(RespireCommandContext context) { Entered++; return null; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        { Completed++; Outcome = outcome; }
    }

    [Test]
    public async Task DifferentSlotsOnOneNodeStillProduceSeparateCommands()
    {
        await using var fixture = await Fixture.CreateAsync();
        RespireKey first = Encoding.UTF8.GetBytes("{a}one");
        RespireKey second = Encoding.UTF8.GetBytes("{e}two");
        await Assert.That(first.ClusterSlot).IsNotEqualTo(second.ClusterSlot);
        await Assert.That((await fixture.Client.Strings.GetManyAsync(first, second)).SequenceEqual(new[] { "{a}one", "{e}two" })).IsTrue();
        await Assert.That(fixture.High.ReceivedCommands.Contains("MGET {a}one")).IsTrue();
        await Assert.That(fixture.High.ReceivedCommands.Contains("MGET {e}two")).IsTrue();
    }

    [Test]
    public async Task InvalidKeyAndFireAndForgetRejectBeforeAnyGroupIsSent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Low.CommandsSeen + fixture.High.CommandsSeen;
        await Assert.That(async () => { using var reply = await fixture.Client.ExecuteAsync("MSET",
                new RespireValue[] { "{a}one", "1", "{b}two", "2", RespireValue.Null, "3" }); })
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await fixture.Client.ExecuteFireAndForgetAsync("DEL", "{a}one", "{b}two"))
            .ThrowsExactly<RespireCrossSlotException>();
        await Assert.That(fixture.Low.CommandsSeen + fixture.High.CommandsSeen).IsEqualTo(before);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task RedirectRetriesOnlyTheRejectedPartition(string redirect)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var destination = new FakeRespServer(8, FakeRespServer.OkReply);
        fixture.High.ReplyOverride = (_, command) => command.StartsWith("MSET ", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes($"-{redirect} {ClusterHash.GetSlot("{a}one")} 127.0.0.1:{destination.Port}\r\n")
            : fixture.Reply(command);
        await Assert.That(await fixture.Client.Strings.SetManyAsync(("{a}one", "1"), ("{b}two", "2"))).IsTrue();
        await Assert.That(fixture.Low.ReceivedCommands.Count(c => c == "MSET {b}two 2")).IsEqualTo(1);
        await Assert.That(destination.ReceivedCommands.Contains("MSET {a}one 1")).IsTrue();
        if (redirect == "ASK") await Assert.That(destination.ReceivedCommands.Contains("ASKING")).IsTrue();
    }

    [Test]
    public async Task ConfiguredRetryDoesNotReplaySuccessfulPartition()
    {
        await using var fixture = await Fixture.CreateAsync(configure: (_, options) => options with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxAttempts = 2,
                MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins },
        });
        var attempts = 0;
        fixture.High.ReplyOverride = (_, command) => command.StartsWith("MSET ", StringComparison.Ordinal)
            && Interlocked.Increment(ref attempts) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : fixture.Reply(command);
        await Assert.That(await fixture.Client.Strings.SetManyAsync(("{a}one", "1"), ("{b}two", "2"))).IsTrue();
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(fixture.Low.ReceivedCommands.Count(c => c == "MSET {b}two 2")).IsEqualTo(1);
    }

    [Test]
    public async Task DisconnectedPartitionUsesBacklogWhileOtherSlotCompletes()
    {
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = false;
        await using var fixture = await Fixture.CreateAsync(configure: (f, options) => options with
        {
            Password = "test",
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == f.High.Port && Volatile.Read(ref gate))
                {
                    connecting.TrySetResult();
                    await resume.Task.WaitAsync(token);
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(host, port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        });
        await fixture.Client.Strings.SetAsync("{a}warm", "value");
        var node = fixture.Client.Core.Cluster!.GetMultiplexer(new("127.0.0.1", fixture.High.Port));
        Volatile.Write(ref gate, true);
        await node.GetConnection().DisposeAsync();
        var lowReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Low.GateCommand("MSET ", Task.CompletedTask, () => lowReceived.TrySetResult());
        var pending = fixture.Client.Strings.SetManyAsync(("{a}one", "1"), ("{b}two", "2")).AsTask();
        try
        {
            await connecting.Task.WaitAsync(Limit);
            await lowReceived.Task.WaitAsync(Limit);
            await Assert.That(pending.IsCompleted).IsFalse();
        }
        finally { resume.TrySetResult(); }
        await Assert.That(await pending.WaitAsync(Limit)).IsTrue();
        await Assert.That(fixture.Low.ReceivedCommands.Count(c => c == "MSET {b}two 2")).IsEqualTo(1);
        await Assert.That(fixture.High.ReceivedCommands.Count(c => c == "MSET {a}one 1")).IsEqualTo(1);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal FakeRespServer Low { get; } = new(8, FakeRespServer.OkReply);
        internal FakeRespServer High { get; } = new(8, FakeRespServer.OkReply);
        internal RespireClient Client { get; private set; } = null!;
        internal byte[] Reply(string command)
        {
            if (command == "CLUSTER SLOTS") return Encoding.ASCII.GetBytes(
                $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{Low.Port}\r\n" +
                $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{High.Port}\r\n");
            if (command.StartsWith("HELLO ", StringComparison.Ordinal)) return "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
            var parts = command.Split(' ');
            if (parts[0] == "MGET") return Encoding.UTF8.GetBytes($"*{parts.Length - 1}\r\n" +
                string.Concat(parts.Skip(1).Select(key => key.EndsWith("nil", StringComparison.Ordinal) ? "$-1\r\n" : $"${Encoding.UTF8.GetByteCount(key)}\r\n{key}\r\n")));
            if (parts[0] is "DEL" or "UNLINK" or "EXISTS" or "TOUCH") return Encoding.ASCII.GetBytes($":{parts.Length - 1}\r\n");
            if (command == "CLIENT ID") return ":1\r\n"u8.ToArray();
            return FakeRespServer.OkReply;
        }
        internal static async Task<Fixture> CreateAsync(bool cache = false, RecordingHook? hook = null,
            Func<Fixture, RespireOptions, RespireOptions>? configure = null)
        {
            var fixture = new Fixture();
            fixture.Low.ReplyOverride = (_, command) => fixture.Reply(command);
            fixture.High.ReplyOverride = (_, command) => fixture.Reply(command);
            var options = new RespireOptions
            {
                UseCluster = true, SplitCrossSlotCommands = true, Connections = 1,
                Endpoints = [new("127.0.0.1", fixture.Low.Port)],
                Protocol = cache ? RespireProtocol.Resp3 : RespireProtocol.Resp2,
                ClientSideCache = cache ? new() : null, CommandHook = hook,
                ClusterTopologyRefreshInterval = null, CommandTimeout = Limit,
            };
            try { fixture.Client = await RespireClient.ConnectAsync(configure?.Invoke(fixture, options) ?? options); }
            catch { await fixture.DisposeAsync(); throw; }
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            await Low.DisposeAsync();
            await High.DisposeAsync();
        }
    }
}
