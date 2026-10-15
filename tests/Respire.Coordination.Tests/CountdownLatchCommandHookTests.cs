using System.Collections.Concurrent;
using System.Text;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class CountdownLatchCommandHookTests
{
    [Test]
    [Arguments("CREATE")]
    [Arguments("RESET")]
    [Arguments("JOIN")]
    [Arguments("COUNTDOWN")]
    [Arguments("WAIT")]
    public async Task RejectionPrecedesEveryWireLeg(string operation)
    {
        await using var server = Server();
        var denied = new InvalidOperationException("Denied latch operation");
        var hook = new RecordingHook { Before = context =>
        {
            if (context.Command == "LATCH " + operation) throw denied;
        } };
        await using var client = await ConnectAsync(server, hook);
        var before = server.CommandsSeen;

        var error = await Failure(() => InvokeAsync(client, operation));

        await Assert.That(error).IsSameReferenceAs(denied);
        await Assert.That(server.CommandsSeen).IsEqualTo(before);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(denied);
    }

    [Test]
    [Arguments("CREATE")]
    [Arguments("RESET")]
    [Arguments("JOIN")]
    [Arguments("COUNTDOWN")]
    [Arguments("WAIT")]
    public async Task SuccessCompletesOnlyTheLogicalOwner(string operation)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);

        await InvokeAsync(client, operation);

        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Command).IsEqualTo("LATCH " + operation);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(hook.Completed.Single().State).IsSameReferenceAs(hook);
    }

    [Test]
    [Arguments("CREATE")]
    [Arguments("RESET")]
    [Arguments("JOIN")]
    [Arguments("COUNTDOWN")]
    [Arguments("WAIT")]
    public async Task InitiallyCanceledCallIsAdmittedWithoutSending(string operation)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = server.CommandsSeen;

        var error = await Failure(() => InvokeAsync(client, operation, cancellation.Token));

        await Assert.That(error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(before);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
    }

    [Test]
    [Arguments("JOIN")]
    [Arguments("COUNTDOWN")]
    [Arguments("WAIT")]
    public async Task ConversionFailureBelongsToLogicalOwner(string operation)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("EVAL", StringComparison.Ordinal)
            ? operation == "COUNTDOWN" ? "$3\r\nbad\r\n"u8.ToArray() : State("bad")
            : FakeRespServer.OkReply;
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);

        var error = await Failure(() => InvokeAsync(client, operation));

        await Assert.That(error).IsTypeOf<RespireProtocolException>();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
    }

    [Test]
    public async Task CompatibilityFallbackIsInsideJoin()
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("EVALSHA_RO ", StringComparison.Ordinal)
            ? "-ERR unknown command 'EVALSHA_RO'\r\n"u8.ToArray()
            : command.StartsWith("EVALSHA ", StringComparison.Ordinal) ? State("0") : FakeRespServer.OkReply;
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);

        var latch = await new RespireCoordination(client).JoinCountdownLatchAsync("latch");

        await Assert.That(latch!.Generation).IsEqualTo("generation");
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Command).IsEqualTo("LATCH JOIN");
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("EVALSHA ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitIncludesSubscriptionAndUnconditionalCleanup(bool cancel)
    {
        await using var server = Server();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("EVALSHA_RO ", StringComparison.Ordinal))
            {
                var read = Interlocked.Increment(ref reads);
                if (cancel && read == 3) cancellation.Cancel();
                return State(read == 1 || cancel ? "1" : "0");
            }
            if (command == "SUBSCRIBE channel") return SubscriptionReply("subscribe", 1);
            if (command == "UNSUBSCRIBE channel") return SubscriptionReply("unsubscribe", 0);
            return FakeRespServer.OkReply;
        };
        var cleanupComplete = false;
        var hook = new RecordingHook
        {
            Before = context =>
            {
                if (context.Command != "LATCH WAIT") throw new InvalidOperationException("Internal leg was admitted");
            },
            After = () => cleanupComplete = server.ReceivedCommands.Contains("UNSUBSCRIBE channel"),
        };
        await using var client = await ConnectAsync(server, hook);
        var latch = new RespireCountdownLatch(client, "latch", "generation", "channel")
        {
            ResyncInterval = TimeSpan.FromMilliseconds(5),
        };
        var error = await Failure(async () => _ = await latch.WaitAsync(cancellation.Token));

        if (cancel) await Assert.That(error).IsAssignableTo<OperationCanceledException>();
        else await Assert.That(error).IsNull();
        await Assert.That(cleanupComplete).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(cancel
            ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments("CREATE")]
    [Arguments("RESET")]
    [Arguments("JOIN")]
    public async Task AdmissionCannotMutateSnapshottedKeyOnPrefixedView(string operation)
    {
        await using var server = Server();
        byte[] key = "latch"u8.ToArray();
        var hook = new RecordingHook { Before = _ => Array.Fill(key, (byte)'x') };
        await using var client = await ConnectAsync(server, hook);
        var coordination = new RespireCoordination(client.WithKeyPrefix("prefix:"));

        var latch = operation switch
        {
            "CREATE" => await coordination.CreateCountdownLatchAsync(key, 1),
            "RESET" => await coordination.ResetCountdownLatchAsync(key, 1),
            _ => await coordination.JoinCountdownLatchAsync(key),
        };

        await Assert.That(latch!.Key.ToString()).IsEqualTo("latch");
        await Assert.That(server.ReceivedCommands.Any(c => c.Contains(" 1 prefix:latch", StringComparison.Ordinal))).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingGenerationCompletesAsSuccessfulResult(bool wait)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("EVALSHA_RO ", StringComparison.Ordinal)
            ? "*1\r\n$1\r\n0\r\n"u8.ToArray() : FakeRespServer.OkReply;
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);

        if (wait)
            await Assert.That(await new RespireCountdownLatch(client, "latch", "generation", "channel").WaitAsync()).IsFalse();
        else
            await Assert.That(await new RespireCoordination(client).JoinCountdownLatchAsync("latch")).IsNull();

        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    public async Task ReentrantApplicationCommandHasItsOwnScopeAndCompletionErrorsAreIgnored()
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
        Task<RespireResult>? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "LATCH COUNTDOWN") nested = client.ExecuteAsync("PING").AsTask();
        };
        hook.After = () => throw new InvalidOperationException("Observer failure");

        await InvokeAsync(client, "COUNTDOWN");
        using var result = await nested!;

        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Any(c => c.Command == "PING")).IsTrue();
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    private static async Task InvokeAsync(RespireClient client, string operation, CancellationToken token = default)
    {
        var coordination = new RespireCoordination(client);
        var latch = new RespireCountdownLatch(client, "latch", "generation", "channel");
        switch (operation)
        {
            case "CREATE": _ = await coordination.CreateCountdownLatchAsync("latch", 1, token); break;
            case "RESET": _ = await coordination.ResetCountdownLatchAsync("latch", 1, token); break;
            case "JOIN": _ = await coordination.JoinCountdownLatchAsync("latch", token); break;
            case "COUNTDOWN": _ = await latch.CountDownAsync(token); break;
            case "WAIT": _ = await latch.WaitAsync(token); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static FakeRespServer Server() => new(2, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command.StartsWith("EVALSHA_RO ", StringComparison.Ordinal) ? State("0")
            : command.StartsWith($"EVALSHA {RespireCoordination.CountDownLatch.Sha1} ", StringComparison.Ordinal)
                ? "$1\r\n0\r\n"u8.ToArray() : FakeRespServer.OkReply,
    };

    private static byte[] State(string remaining)
        => Encoding.ASCII.GetBytes($"*4\r\n$1\r\n1\r\n$10\r\ngeneration\r\n${remaining.Length}\r\n{remaining}\r\n$7\r\nchannel\r\n");

    private static byte[] SubscriptionReply(string operation, int count)
        => Encoding.ASCII.GetBytes($"*3\r\n${operation.Length}\r\n{operation}\r\n$7\r\nchannel\r\n:{count}\r\n");

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server, RecordingHook hook)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            CommandHook = hook, ThreadPoolMonitoring = false,
        });

    private static async Task<Exception?> Failure(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal Action? After;
        internal ConcurrentQueue<(string Command, RespireCommandOutcome Outcome, Exception? Error, object? State)> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context) { Before?.Invoke(context); return this; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context.Command, outcome, error, state));
            After?.Invoke();
        }
    }
}
