using System.Collections.Concurrent;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CommandHookTests
{
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
        var hook = new RecordingHook();
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
        await using var primary = Server();
        await using var replica = new FakeRespServer(
            "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal),
        };
        var hook = new RecordingHook();
        await using var client = RespireClient.Create(Options(primary, hook) with
        {
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(10), MaximumExtraLoadPercent = 100 },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("42");
        await Assert.That(primary.ReceivedCommands).Contains("GET key");
        await Assert.That(replica.ReceivedCommands).Contains("GET key");
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await replica.SendRawAsync("$5\r\nloser\r\n"u8.ToArray());
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
            case "lease": using (await client.ExecuteLeaseAsync("GET", "key")) { } break;
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
