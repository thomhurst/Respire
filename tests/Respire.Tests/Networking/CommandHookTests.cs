using System.Collections.Concurrent;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CommandHookTests
{
    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("forget")]
    public async Task SubcommandAdmissionUsesTheSameNameForEveryDescriptor(string route)
    {
        await using var server = Server();
        var rejected = new InvalidOperationException("SCRIPT FLUSH denied");
        var hook = new RecordingHook
        {
            Before = context => { if (context.Command == "SCRIPT FLUSH") throw rejected; },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;
        (RespireCommand Command, RespireValue[] Args)[] cases =
        [
            (RespireCommands.Scripting.SCRIPT, ["FLUSH"]),
            (RespireCommands.Scripting.SCRIPT_FLUSH, []),
            ((RespireCommand)"SCRIPT", ["FLUSH"]),
            ((RespireCommand)"script flush", []),
        ];
        foreach (var (command, args) in cases)
        {
            var error = await Failure(async () =>
            {
                if (route == "forget") await client.ExecuteFireAndForgetAsync(command, args);
                else if (route == "lease") { using var result = await client.ExecuteLeaseAsync(command, args); }
                else { using var result = await client.ExecuteAsync(command, args); }
            });
            await Assert.That(error).IsSameReferenceAs(rejected);
        }
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Entered.Count).IsEqualTo(cases.Length);
        await Assert.That(hook.Entered.All(c => c.Command == "SCRIPT FLUSH")).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(cases.Length);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Rejected
            && ReferenceEquals(c.Error, rejected))).IsTrue();
    }

    [Test]
    [Arguments("owned", false, false)]
    [Arguments("owned", false, true)]
    [Arguments("owned", true, false)]
    [Arguments("owned", true, true)]
    [Arguments("lease", false, false)]
    [Arguments("lease", false, true)]
    [Arguments("lease", true, false)]
    [Arguments("lease", true, true)]
    [Arguments("forget", false, false)]
    [Arguments("forget", false, true)]
    [Arguments("forget", true, false)]
    [Arguments("forget", true, true)]
    public async Task PreflightErrorsDoNotInvokeHooks(string route, bool catalog, bool reject)
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook { Before = reject ? _ => throw new InvalidOperationException("Rejected") : null };
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)], UseCluster = true,
            Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
        };
        await using var plain = RespireClient.Create(options);
        await using var hooked = RespireClient.Create(options with { CommandHook = hook });
        (string Name, RespireValue[] Args, bool Prefix, RespireCommandFlags Flags)[] cases =
        [
            ("MGET", ["{a}:one", "{b}:two"], false, RespireCommandFlags.None),
            ("JSON.MGET", ["{a}:one", "{b}:two", "$"], true, RespireCommandFlags.None),
        ];
        foreach (var item in cases)
            await Check(item.Name, item.Args, item.Prefix, item.Flags);
        if (catalog)
        {
            await Check("SELECT", [1], false, RespireCommandFlags.None);
            await Check("KEYS", ["*"], true, RespireCommandFlags.None);
            await Check("GET", [RespireValue.Null], true, RespireCommandFlags.None);
        }
        if (route == "forget")
        {
            await Check("BLPOP", ["key", 0], false, RespireCommandFlags.None);
            if (!catalog) await Check("SELECT", [1], false, RespireCommandFlags.None);
        }
        else
        {
            await Check("GET", ["key"], false, (RespireCommandFlags)128);
            await Check("FUNCTION FLUSH", [], false, RespireCommandFlags.NoRedirect);
        }
        if (!catalog)
            await Check("MGET {a}:one", ["{b}:two"], false, RespireCommandFlags.None);

        await Assert.That(hook.Entered.Count).IsEqualTo(0);
        await Assert.That(hook.Completed.Count).IsEqualTo(0);
        await Assert.That(plain.IsConnected).IsFalse();
        await Assert.That(hooked.IsConnected).IsFalse();

        async Task Check(string name, RespireValue[] args, bool prefix, RespireCommandFlags flags)
        {
            var command = catalog ? RespireCommands.All.ToArray().Single(c => c.Name == name) : (RespireCommand)name;
            var expected = await Failure(() => Send(prefix ? plain.WithKeyPrefix("prefix:") : plain));
            var actual = await Failure(() => Send(prefix ? hooked.WithKeyPrefix("prefix:") : hooked));
            await Assert.That(actual.GetType()).IsEqualTo(expected.GetType());
            await Assert.That(actual.Message).IsEqualTo(expected.Message);

            async Task Send(IRespireClient client)
            {
                if (route == "forget") await client.ExecuteFireAndForgetAsync(command, args);
                else if (route == "lease") { using var result = await client.ExecuteLeaseAsync(command, args, flags); }
                else { using var result = await client.ExecuteAsync(command, args, flags); }
            }
        }
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("facet")]
    [Arguments("converted")]
    [Arguments("raw")]
    [Arguments("catalog")]
    [Arguments("lease")]
    [Arguments("forget")]
    public async Task AdmissionPrecedesWireAndCompletionPreservesState(string route)
    {
        await using var server = Server();
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;
        hook.Before = _ =>
        {
            if (server.CommandsSeen != baseline) throw new InvalidOperationException("Dispatched before admission.");
        };
        await Execute(client, route);
        var result = hook.Completed.Single();
        await Assert.That(result.Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(result.Error).IsNull();
        await Assert.That(ReferenceEquals(result.State, result.Context)).IsTrue();
        await Assert.That(hook.Entered.Single()).IsSameReferenceAs(result.Context);
        await Assert.That(result.Context.Command).IsEqualTo(route == "converted" ? "STRLEN" : "GET");
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("facet")]
    [Arguments("converted")]
    [Arguments("raw")]
    [Arguments("catalog")]
    [Arguments("lease")]
    [Arguments("forget")]
    public async Task RejectionSendsNothingAndReportsTheSameException(string route)
    {
        await using var server = Server();
        var rejected = new InvalidOperationException("Rejected");
        var hook = new RecordingHook { Before = _ => throw rejected, ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;
        var error = await Failure(() => Execute(client, route));
        await Assert.That(ReferenceEquals(error, rejected)).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        var result = hook.Completed.Single();
        await Assert.That(result.Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(ReferenceEquals(result.Error, error)).IsTrue();
        await Assert.That(result.State).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerAndConversionFailuresAreFinal(bool conversion)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? conversion ? "$3\r\nbad\r\n"u8.ToArray() : "-ERR failed\r\n"u8.ToArray() : null;
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var error = await Failure(async () => _ = await client.GetAsync<int>("key"));
        var result = hook.Completed.Single();
        await Assert.That(result.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(ReferenceEquals(result.Error, error)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationIsReportedOnceBeforeAndAfterDispatch(bool pending)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server();
        server.SuppressReply = command =>
        {
            if (command != "GET key") return false;
            received.TrySetResult();
            return true;
        };
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var cancellation = new CancellationTokenSource();
        if (!pending) cancellation.Cancel();
        var response = client.GetStringAsync("key", cancellation.Token).AsTask();
        if (pending)
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        var error = await Failure(async () => await response);
        var result = hook.Completed.Single();
        await Assert.That(result.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(ReferenceEquals(result.Error, error)).IsTrue();
        await Assert.That(result.Context.CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("query")]
    [Arguments("raw")]
    [Arguments("raw-coalesced")]
    public async Task ResidentHitsAreAdmittedAndCanBeRejected(string route)
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)],
            ClientSideCache = new() { CoalesceConcurrentMisses = route == "raw-coalesced" },
            CommandHook = hook, ThreadPoolMonitoring = false,
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var value = RespValue.BulkString("42"u8.ToArray());
        var read = cache.BeginRead(in key);
        cache.CompleteRead(in read, in value, allowInsert: true);
        // Non-coalesced raw GET uses the command-query cache, not the typed GET entry.
        var rawRequest = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("GET", key.AsValue()), key);
        var rawRead = cache.BeginRead("GET", in rawRequest);
        cache.CompleteRead(in rawRead, in value, allowInsert: true);
        var request = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("STRLEN", key.AsValue()), key);
        var queryRead = cache.BeginRead("STRLEN", in request);
        var length = RespValue.Integer(2);
        cache.CompleteRead(in queryRead, in length, allowInsert: true);
        var executionRoute = route == "query" ? "converted" : route == "raw-coalesced" ? "raw" : route;
        await Execute(client, executionRoute);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        hook.Before = _ => throw new InvalidOperationException("Cache denied");
        await Failure(() => Execute(client, executionRoute));
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SharedCacheMissesCompleteEachCallerIndependently(bool cancelLeader)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "GET key") return false;
                received.TrySetResult();
                return true;
            },
        };
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        using var cancellation = new CancellationTokenSource();
        var leader = client.GetStringAsync("key", cancelLeader ? cancellation.Token : default).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var follower = client.GetStringAsync("key", cancelLeader ? default : cancellation.Token).AsTask();
        var contexts = hook.Entered.ToArray();
        await Assert.That(contexts.Length).IsEqualTo(2);
        await Assert.That(ReferenceEquals(contexts[0], contexts[1])).IsFalse();
        await Assert.That(hook.Completed).IsEmpty();

        cancellation.Cancel();
        var canceled = cancelLeader ? leader : follower;
        var surviving = cancelLeader ? follower : leader;
        var error = await Failure(async () => await canceled.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That(error is OperationCanceledException).IsTrue();
        var canceledCompletion = hook.Completed.Single();
        await Assert.That(canceledCompletion.Context).IsSameReferenceAs(contexts[cancelLeader ? 0 : 1]);
        await Assert.That(canceledCompletion.State).IsSameReferenceAs(canceledCompletion.Context);
        await Assert.That(canceledCompletion.Context.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(canceledCompletion.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(canceledCompletion.Error).IsSameReferenceAs(error);
        await Assert.That(surviving.IsCompleted).IsFalse();

        await server.SendRawAsync("$2\r\n42\r\n"u8.ToArray());
        await Assert.That(await surviving.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("42");
        var completions = hook.Completed.ToArray();
        await Assert.That(completions.Length).IsEqualTo(2);
        await Assert.That(completions[1].Context).IsSameReferenceAs(contexts[cancelLeader ? 1 : 0]);
        await Assert.That(completions[1].State).IsSameReferenceAs(completions[1].Context);
        await Assert.That(completions[1].Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(completions[1].Error).IsNull();
        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task RetriesShareOneAdmissionAndCompletion()
    {
        var attempts = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref attempts) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : "$2\r\n42\r\n"u8.ToArray()
            : null;
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero } });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("42");
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    public async Task HedgeWinnerCompletesOneLogicalRead()
    {
        var primaryReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicaReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = Server();
        primary.SuppressReply = command => HoldRead(command, primaryReceived);
        await using var replica = new FakeRespServer(
            "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray())
        {
            SuppressReply = command => HoldRead(command, replicaReceived),
            ReplyOverride = (_, command) => command == "PING" ? FakeRespServer.PongReply : null,
        };
        var hook = new RecordingHook();
        await using var client = RespireClient.Create(Options(primary, hook) with
        {
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(10), MaximumExtraLoadPercent = 100 },
        });
        // Cold connection/ROLE setup consumed the old read deadline under concurrent load.
        // Establish both routes without admitting an application command before timing the race.
        using var setupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var replicaConnection = await client.Core.ReadRouter.GetConnectionAsync(
            RespireReadFrom.Replica, setupDeadline.Token, observation: default);
        var primaryConnection = await client.Core.ReadRouter.GetHedgeConnectionAsync(
            RespireReadFrom.ReplicaPreferred, replicaConnection, setupDeadline.Token);
        await Assert.That(replicaConnection.Port).IsEqualTo(replica.Port);
        await Assert.That(primaryConnection?.Port).IsEqualTo(primary.Port);
        await Assert.That(hook.Entered).IsEmpty();
        await Assert.That(hook.Completed).IsEmpty();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = client.GetStringAsync("key", deadline.Token).AsTask();
        await Task.WhenAll(primaryReceived.Task, replicaReceived.Task).WaitAsync(deadline.Token);
        await Assert.That(read.IsCompleted).IsFalse();
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(hook.Completed).IsEmpty();

        // Publish the winner while the original reply is still withheld. Completion must not
        // wait for the loser, and both physical sends must belong to this one logical context.
        await primary.SendRawAsync("$2\r\n42\r\n"u8.ToArray()).WaitAsync(deadline.Token);
        await Assert.That(await read.WaitAsync(deadline.Token)).IsEqualTo("42");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context).IsSameReferenceAs(hook.Entered.Single());
        await Assert.That(completion.State).IsSameReferenceAs(completion.Context);
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(completion.Error).IsNull();

        // A direct PING after the loser proves its FIFO slot was drained and the same socket
        // remains usable, without introducing another application hook scope.
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await replica.SendRawAsync("$5\r\nloser\r\n"u8.ToArray()).WaitAsync(cleanupDeadline.Token);
        using var pong = await replicaConnection.SendAsync(new Cmd(new Verb("PING")), cleanupDeadline.Token);
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);

        static bool HoldRead(string command, TaskCompletionSource received)
        {
            if (command != "GET key") return false;
            received.TrySetResult();
            return true;
        }
    }

    [Test]
    public async Task ReentrantAndConcurrentCommandsHaveIndependentContexts()
    {
        await using var server = Server();
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var reentrant = new ConcurrentQueue<Task<long>>();
        hook.Before = context =>
        {
            if (context.Command == "GET") reentrant.Enqueue(client.Strings.LengthAsync("key").AsTask());
        };
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetStringAsync("key").AsTask()));
        await Task.WhenAll(reentrant);
        await Assert.That(hook.Entered.Count).IsEqualTo(24);
        await Assert.That(hook.Completed.Count).IsEqualTo(24);
        await Assert.That(hook.Entered.Distinct().Count()).IsEqualTo(24);
        await Assert.That(hook.Completed.All(r => ReferenceEquals(r.State, r.Context)
            && r.Outcome == RespireCommandOutcome.Succeeded && r.Error is null)).IsTrue();
    }

    [Test]
    public async Task RawNameOmitsInlineArgumentsAndViewsShareTheHook()
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var result = await client.ExecuteAsync("get secret-key");
        await client.WithKeyPrefix("prefix:").Strings.GetStringAsync("key");
        await Assert.That(hook.Entered.All(c => c.Command == "GET")).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Contains("GET prefix:key")).IsTrue();
    }

    private static async Task Execute(RespireClient client, string route)
    {
        switch (route)
        {
            case "string": await client.GetStringAsync("key"); break;
            case "bytes": await client.GetBytesAsync("key"); break;
            case "typed": await client.GetAsync<int>("key"); break;
            case "facet": await client.Strings.GetStringAsync("key"); break;
            case "converted": await client.Strings.LengthAsync("key"); break;
            case "raw": using (await client.ExecuteAsync("GET", "key")) { } break;
            case "catalog": using (await client.ExecuteAsync(RespireCommands.String.GET, "key")) { } break;
            case "lease":
                using (var result = await client.ExecuteLeaseAsync("GET", "key"))
                    await Assert.That(result.AsString()).IsEqualTo("42");
                break;
            case "forget": await client.ExecuteFireAndForgetAsync("GET", "key"); break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static FakeRespServer Server() => new(64, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? "$2\r\n42\r\n"u8.ToArray()
            : command.StartsWith("STRLEN ", StringComparison.Ordinal) ? ":2\r\n"u8.ToArray() : null,
    };

    private static RespireOptions Options(FakeRespServer server, RespireCommandHook hook) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5), CommandHook = hook,
    };

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal bool ThrowOnCompleted;
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(RespireCommandContext Context, object? State, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, state, outcome, error));
            if (ThrowOnCompleted) throw new InvalidOperationException("Observer failure");
        }
    }
}
