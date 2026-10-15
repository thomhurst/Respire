using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SpecializedCommandHookTests
{
    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("span")]
    [Arguments("lease-span")]
    [Arguments("integer")]
    [Arguments("string")]
    [Arguments("typed")]
    [Arguments("blocking")]
    [Arguments("cursor")]
    [Arguments("keys")]
    public async Task RejectionPreventsApplicationDispatch(string route)
    {
        await using var server = new FakeRespServer(4, ":42\r\n"u8.ToArray());
        var rejection = new InvalidOperationException("Denied");
        var hook = new RecordingHook { Before = _ => throw rejection };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;

        var error = await Failure(() => Execute(client, route));

        await Assert.That(error).IsSameReferenceAs(rejection);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Context.Command).IsEqualTo(Command(route));
        await Assert.That(completed.Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(completed.Error).IsSameReferenceAs(rejection);
        await Assert.That(completed.State).IsNull();
    }

    [Test]
    [Arguments("owned", false)]
    [Arguments("lease", false)]
    [Arguments("span", false)]
    [Arguments("lease-span", false)]
    [Arguments("integer", false)]
    [Arguments("string", false)]
    [Arguments("typed", false)]
    [Arguments("integer", true)]
    [Arguments("lease", true)]
    public async Task ScriptFallbackCompletesOnceAfterFinalResult(string route, bool readOnly)
    {
        await using var server = new FakeRespServer("-NOSCRIPT missing\r\n"u8.ToArray(), ":42\r\n"u8.ToArray());
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        hook.Before = _ =>
        {
            if (server.CommandsSeen != 0) throw new InvalidOperationException("Admission was late");
        };

        await Execute(client, route, readOnly: readOnly);

        await Assert.That(server.CommandsSeen).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands[0].StartsWith(readOnly ? "EVALSHA_RO " : "EVALSHA ")).IsTrue();
        await Assert.That(server.ReceivedCommands[1].StartsWith(readOnly ? "EVAL_RO " : "EVAL ")).IsTrue();
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(completed.Context.Command).IsEqualTo(readOnly ? "EVALSHA_RO" : "EVALSHA");
        await Assert.That(completed.State).IsSameReferenceAs(completed.Context);
        await Assert.That(completed.Error).IsNull();
    }

    [Test]
    [Arguments("typed")]
    [Arguments("blocking")]
    [Arguments("cursor")]
    [Arguments("keys")]
    public async Task ConversionFailureIsTheFinalOutcome(string route)
    {
        await using var server = new FakeRespServer(4, "+not-a-valid-result\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));

        var error = await Failure(() => Execute(client, route));

        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completed.Error).IsSameReferenceAs(error);
    }

    [Test]
    [Arguments("owned")]
    [Arguments("integer")]
    [Arguments("blocking")]
    [Arguments("cursor")]
    [Arguments("keys")]
    public async Task CancellationAfterAdmissionCompletesOnceWithoutSending(string route)
    {
        await using var server = new FakeRespServer(4, ":42\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var hook = new RecordingHook { Before = _ => cancellation.Cancel() };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));

        var error = await Failure(() => Execute(client, route, cancellation.Token));

        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
    }

    [Test]
    [Arguments("cursor", false)]
    [Arguments("hash-cursor", false)]
    [Arguments("sorted-set-cursor", false)]
    [Arguments("keys", false)]
    [Arguments("blocking-move", false)]
    [Arguments("move", false)]
    [Arguments("cursor", true)]
    [Arguments("hash-cursor", true)]
    [Arguments("sorted-set-cursor", true)]
    [Arguments("keys", true)]
    [Arguments("blocking-move", true)]
    [Arguments("move", true)]
    [Arguments("stream-group", false)]
    [Arguments("stream-group-options", false)]
    [Arguments("stream-group-pending", false)]
    [Arguments("stream-group-options-pending", false)]
    [Arguments("stream-group", true)]
    [Arguments("stream-group-options", true)]
    [Arguments("stream-group-pending", true)]
    [Arguments("stream-group-options-pending", true)]
    public async Task AlreadyCanceledCommandsAdmitBeforeCheckingCancellation(string route, bool reject)
    {
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var rejection = new InvalidOperationException("Denied");
        var hook = new RecordingHook { Before = _ => { if (reject) throw rejection; } };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;

        var error = await Failure(() => Execute(client, route, cancellation.Token));

        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Context.Command).IsEqualTo(Command(route));
        await Assert.That(completed.Context.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(completed.Error).IsSameReferenceAs(error);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        if (reject)
        {
            await Assert.That(error).IsSameReferenceAs(rejection);
            await Assert.That(completed.Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
            await Assert.That(completed.State).IsNull();
        }
        else
        {
            await Assert.That(error is OperationCanceledException).IsTrue();
            await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(completed.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            await Assert.That(completed.State).IsSameReferenceAs(completed.Context);
        }
    }

    [Test]
    public async Task EachCursorPageHasItsOwnScopeAndCanBeRejected()
    {
        await using var server = new FakeRespServer("*2\r\n$1\r\n1\r\n*1\r\n$3\r\none\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var enumerator = client.Sets.ScanAsync("set").GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current).IsEqualTo("one");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        hook.Before = _ => throw new InvalidOperationException("Page denied");

        await Failure(async () => { _ = await enumerator.MoveNextAsync(); });

        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
    }

    [Test]
    public async Task StreamCancellationBetweenEntriesDoesNotOpenAnotherPageScope()
    {
        await using var server = new FakeRespServer(4,
            "*1\r\n*2\r\n$6\r\nstream\r\n*2\r\n*2\r\n$3\r\n1-0\r\n*2\r\n$1\r\nf\r\n$1\r\nv\r\n*2\r\n$3\r\n2-0\r\n*2\r\n$1\r\nf\r\n$1\r\nv\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var enumerator = client.Streams.ReadGroupAsync(new StreamReadOptions(), "stream", "group", "consumer",
            cancellationToken: cancellation.Token).GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        var commands = server.CommandsSeen;
        cancellation.Cancel();

        var error = await Failure(async () => { _ = await enumerator.MoveNextAsync(); });

        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
    }

    [Test]
    public async Task BlockingCompletionIncludesConvertedResult()
    {
        await using var server = new FakeRespServer(4, "*2\r\n$4\r\nlist\r\n$5\r\nvalue\r\n"u8.ToArray());
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));

        var result = await client.Lists.PopAsync(["list"], TimeSpan.FromSeconds(1));

        await Assert.That(result).IsNotNull();
        await Assert.That(hook.Entered.Single().Command).IsEqualTo("BLPOP");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RawScriptResultRemainsUsableAfterCompletion(bool leased)
    {
        await using var server = new FakeRespServer("*2\r\n:42\r\n$5\r\nvalue\r\n"u8.ToArray());
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var script = RespireScript.Create("return {42, 'value'}");

        using var result = leased
            ? await client.Scripts.ExecuteLeaseAsync(script)
            : await client.Scripts.ExecuteAsync(script);

        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(result[0].AsInteger()).IsEqualTo(42);
        await Assert.That(result[1].AsString()).IsEqualTo("value");
        // Caller-selected conversion is deliberately outside the already completed raw result.
        await Failure(() => { _ = result[1].AsDouble(); return Task.CompletedTask; });
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ReentrantScriptCallHasAnIndependentScope()
    {
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        Task? nested = null;
        hook.Before = _ =>
        {
            if (hook.Entered.Count == 1) nested = Execute(client, "integer");
        };

        await Execute(client, "integer");
        await nested!;

        await Assert.That(server.CommandsSeen).IsEqualTo(2);
        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(hook.Entered.First(), hook.Entered.Last())).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeclaredRetriesAndFallbackShareTheFinalOutcome(bool fail)
    {
        await using var server = new FakeRespServer(
            "-TRYAGAIN busy\r\n"u8.ToArray(), "-NOSCRIPT missing\r\n"u8.ToArray(),
            fail ? "-ERR script failed\r\n"u8.ToArray() : ":42\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero } });
        var script = RespireScript.Create("return 42").WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        Exception? error = null;

        if (fail) error = await Failure(async () => { _ = await client.Scripts.ExecuteIntegerAsync(script); });
        else await Assert.That(await client.Scripts.ExecuteIntegerAsync(script)).IsEqualTo(42);

        await Assert.That(server.CommandsSeen).IsEqualTo(3);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Outcome).IsEqualTo(fail ? RespireCommandOutcome.Failed : RespireCommandOutcome.Succeeded);
        await Assert.That(ReferenceEquals(completed.Error, error)).IsTrue();
    }

    [Test]
    [Arguments("owned")]
    [Arguments("integer")]
    [Arguments("blocking")]
    [Arguments("cursor")]
    public async Task CancellationWhileAwaitingReplyIsReportedAtCompletion(string route)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith(Command(route) + " ", StringComparison.Ordinal)) return false;
                sent.TrySetResult();
                return true;
            },
        };
        using var cancellation = new CancellationTokenSource();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var pending = Execute(client, route, cancellation.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(hook.Completed.Count).IsEqualTo(0);

        cancellation.Cancel();
        var error = await Failure(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
    }

    private static string Command(string route) => route switch
    {
        "blocking" => "BLPOP", "cursor" => "SSCAN", "keys" => "SCAN",
        "hash-cursor" => "HSCAN", "sorted-set-cursor" => "ZSCAN",
        "stream-group" or "stream-group-options" or "stream-group-pending" or "stream-group-options-pending" => "XREADGROUP",
        "blocking-move" => "BLMOVEM", "move" => "LMOVEM", _ => "EVALSHA",
    };

    private static async Task Execute(RespireClient client, string route, CancellationToken token = default, bool readOnly = false)
    {
        var script = RespireScript.Create("return 42", readOnly);
        switch (route)
        {
            case "owned": using (await client.Scripts.ExecuteAsync(script, cancellationToken: token)) { } break;
            case "lease": using (await client.Scripts.ExecuteLeaseAsync(script, cancellationToken: token)) { } break;
            case "span": using (await client.Scripts.ExecuteSpanAsync(script, [], [], token)) { } break;
            case "lease-span": using (await client.Scripts.ExecuteLeaseSpanAsync(script, [], [], token)) { } break;
            case "integer": _ = await client.Scripts.ExecuteIntegerAsync(script, cancellationToken: token); break;
            case "string": _ = await client.Scripts.ExecuteStringAsync(script, cancellationToken: token); break;
            case "typed": _ = await client.Scripts.ExecuteAsync<long>(script, cancellationToken: token); break;
            case "blocking": _ = await client.Lists.PopAsync(["list"], TimeSpan.FromSeconds(1), cancellationToken: token); break;
            case "cursor": await foreach (var _ in client.Sets.ScanAsync("set", cancellationToken: token)) { } break;
            case "keys": await foreach (var _ in client.Keys.ScanAsync(cancellationToken: token)) { } break;
            case "hash-cursor": await foreach (var _ in client.Hashes.ScanAsync("hash", cancellationToken: token)) { } break;
            case "sorted-set-cursor": await foreach (var _ in client.SortedSets.ScanAsync("sorted-set", cancellationToken: token)) { } break;
            case "blocking-move": _ = await client.Lists.MoveManyAsync("source", "destination", TimeSpan.FromSeconds(1), cancellationToken: token); break;
            case "move": _ = await client.Lists.MoveManyAsync("source", "destination", cancellationToken: token); break;
            case "stream-group": await foreach (var _ in client.Streams.ReadGroupAsync("stream", "group", "consumer", cancellationToken: token)) { } break;
            case "stream-group-options": await foreach (var _ in client.Streams.ReadGroupAsync(new StreamReadOptions(), "stream", "group", "consumer", cancellationToken: token)) { } break;
            case "stream-group-pending": await foreach (var _ in client.Streams.ReadGroupAsync("stream", "group", "consumer", RespireStreamId.Beginning, cancellationToken: token)) { } break;
            case "stream-group-options-pending": await foreach (var _ in client.Streams.ReadGroupAsync(new StreamReadOptions(), "stream", "group", "consumer", RespireStreamId.Beginning, cancellationToken: token)) { } break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

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
