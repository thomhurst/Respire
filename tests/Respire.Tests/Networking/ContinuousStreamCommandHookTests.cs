using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ContinuousStreamCommandHookTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectionPrecedesInitialResolutionAndIsNeverRetried(bool newest)
    {
        await using var server = new FakeRespServer(4, StreamReadTests.Reply(false, ("events", ["1-0"])));
        var denied = new RespireConnectionException("Policy rejection is not a transport failure");
        var hook = new Hook { Before = _ => throw denied };
        await using var client = Create(server, hook);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = client.Streams.ReadAllAsync("events",
            newest ? RespireStreamId.New : RespireStreamId.Beginning, cancellationToken: deadline.Token).GetAsyncEnumerator();

        var error = await Failure(async () => await reader.MoveNextAsync());

        await Assert.That(error).IsSameReferenceAs(denied);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(hook.Completed.Single().Command).IsEqualTo("XREAD");
    }

    [Test]
    public async Task ResolutionRecoveryAndBufferedEntriesShareOnePageScope()
    {
        var reads = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command.StartsWith("XREVRANGE ", StringComparison.Ordinal)
            ? StreamReadTests.Entries(["7-0"])
            : Interlocked.Increment(ref reads) == 1
                ? "-CLUSTERDOWN temporary\r\n"u8.ToArray()
                : StreamReadTests.Reply(false, ("events", ["8-0", "9-0"]));
        var hook = new Hook();
        await using var client = Create(server, hook);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = client.Streams.ReadAllAsync("events", RespireStreamId.New,
            cancellationToken: deadline.Token).GetAsyncEnumerator();

        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Id).IsEqualTo((RespireStreamId)"8-0");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Id).IsEqualTo((RespireStreamId)"9-0");
        await Assert.That(server.CommandsSeen).IsEqualTo(3);
        var denied = new InvalidOperationException("No next page");
        hook.Before = _ => throw denied;
        await Assert.That(await Failure(async () => await reader.MoveNextAsync())).IsSameReferenceAs(denied);
        await Assert.That(server.CommandsSeen).IsEqualTo(3);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("other", "1-0", "2-0")]
    [Arguments("events", "2-0", "1-0")]
    [Arguments("events", "bad", "2-0")]
    public async Task EntirePageValidationFinishesBeforeAnyEntryIsPublished(string key, string first, string second)
    {
        await using var server = new FakeRespServer(StreamReadTests.Reply(false, (key, [first, second])));
        var hook = new Hook();
        await using var client = Create(server, hook);
        await using var reader = client.Streams.ReadAllAsync([("events", (RespireStreamId)"0")]).GetAsyncEnumerator();

        var error = await Failure(async () => await reader.MoveNextAsync());

        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task InitiallyCanceledPageIsAdmittedWithoutSending()
    {
        await using var server = new FakeRespServer();
        var hook = new Hook();
        await using var client = Create(server, hook);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await using var reader = client.Streams.ReadAllAsync("events", cancellationToken: cancel.Token).GetAsyncEnumerator();

        var error = await Failure(async () => await reader.MoveNextAsync());

        await Assert.That(error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GroupAlreadyExistsIsSuccessfulButAdmissionRejectionPropagates(bool createStream)
    {
        await using var server = new FakeRespServer("-BUSYGROUP already exists\r\n"u8.ToArray());
        var hook = new Hook();
        await using var client = Create(server, hook);
        await Assert.That(await client.Streams.CreateGroupAsync("events", "group", createStream: createStream)).IsFalse();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        var denied = new RespireServerException("BUSYGROUP policy rejection");
        hook.Before = _ => throw denied;

        var error = await Failure(async () => await client.Streams.CreateGroupAsync("events", "group", createStream: createStream));

        await Assert.That(error).IsSameReferenceAs(denied);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
    }

    [Test]
    public async Task ReentrantApplicationCommandHasItsOwnScopeAndObserverErrorsAreIgnored()
    {
        await using var server = new FakeRespServer(4, StreamReadTests.Reply(false, ("events", ["1-0"])));
        server.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? "$2\r\n42\r\n"u8.ToArray() : StreamReadTests.Reply(false, ("events", ["1-0"]));
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = Create(server, hook);
        Task<string?>? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "XREAD") nested = client.GetStringAsync("other").AsTask();
        };
        await using var reader = client.Streams.ReadAllAsync("events").GetAsyncEnumerator();

        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(await nested!).IsEqualTo("42");
        await Assert.That(hook.Completed.Select(c => c.Command).Order().ToArray()).IsEquivalentTo(new[] { "GET", "XREAD" });
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    [Test]
    public async Task CancellationDuringReadCompletesOnceAndDisposalDoesNotReadAgain()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { reading.TrySetResult(); return true; } };
        var hook = new Hook();
        await using var client = Create(server, hook);
        using var cancel = new CancellationTokenSource();
        await using var reader = client.Streams.ReadAllAsync("events", cancellationToken: cancel.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();

        var error = await Failure(async () => await next.WaitAsync(TimeSpan.FromSeconds(5)));
        await reader.DisposeAsync();

        await Assert.That(error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GroupReplyAndConversionCompleteOnce(bool malformed)
    {
        await using var server = new FakeRespServer(malformed ? ":1\r\n"u8.ToArray() : FakeRespServer.OkReply);
        var hook = new Hook();
        await using var client = Create(server, hook);
        if (malformed)
        {
            var error = await Failure(async () => await client.Streams.CreateGroupAsync("events", "group"));
            await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        }
        else
            await Assert.That(await client.Streams.CreateGroupAsync("events", "group")).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(
            malformed ? RespireCommandOutcome.Failed : RespireCommandOutcome.Succeeded);
    }

    private static RespireClient Create(FakeRespServer server, Hook hook)
        => RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)], CommandHook = hook,
        });

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure");
    }

    private sealed class Hook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal bool ThrowOnCompleted;
        internal ConcurrentQueue<(string Command, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context) { Before?.Invoke(context); return context; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context.Command, outcome, error));
            if (ThrowOnCompleted) throw new InvalidOperationException("Observer");
        }
    }
}
