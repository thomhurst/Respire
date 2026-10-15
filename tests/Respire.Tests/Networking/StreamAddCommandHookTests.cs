using System.Collections.Concurrent;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamAddCommandHookTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AdmissionRejectsAndCancelsWithoutSending(bool automatic, bool optional)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook, automatic));
        var rejected = new InvalidOperationException("Rejected XADD");
        hook.Before = _ => throw rejected;
        await Assert.That(async () => await Add(client, optional)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(rejected);
        await Assert.That(AddCount(server)).IsEqualTo(0);

        hook.Completed.Clear();
        hook.Before = null;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await Add(client, optional, canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(hook.Completed.Single().Context.CancellationToken).IsEqualTo(canceled.Token);
        await Assert.That(AddCount(server)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AutomaticIdentityPublishesOneFinalOutcome(bool fallback, bool optional)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        var fail = false;
        server.ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith("XADD ", StringComparison.Ordinal)) return null;
            if (fallback && command.Contains(" IDMP ", StringComparison.Ordinal))
                return "-ERR Invalid stream ID specified as stream command argument\r\n"u8.ToArray();
            return fail ? "-ERR final failure\r\n"u8.ToArray() : "$3\r\n1-0\r\n"u8.ToArray();
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await Assert.That(await Add(client, optional)).IsEqualTo((RespireStreamId?)"1-0");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(hook.Completed.Single().Context.Command).IsEqualTo("XADD");
        await Assert.That(AddCount(server)).IsEqualTo(fallback ? 2 : 1);

        hook.Completed.Clear();
        fail = true;
        await Assert.That(async () => await Add(client, optional)).ThrowsExactly<RespireServerException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(hook.Completed.Single().Error!.Message).IsEqualTo("ERR final failure");
        await Assert.That(AddCount(server)).IsEqualTo(fallback ? 4 : 2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequiredNullFailsInsideScopeWhileOptionalNullSucceeds(bool fallback)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => !command.StartsWith("XADD ", StringComparison.Ordinal) ? null
            : fallback && command.Contains(" IDMP ", StringComparison.Ordinal)
                ? "-ERR Invalid stream ID specified as stream command argument\r\n"u8.ToArray()
                : "$-1\r\n"u8.ToArray();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await Assert.That(async () => await Add(client, optional: false)).ThrowsExactly<RespireProtocolException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(hook.Completed.Single().Error is RespireProtocolException).IsTrue();
        hook.Completed.Clear();
        await Assert.That(await Add(client, optional: true)).IsNull();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrdinaryAndExplicitIdempotencyPublishConvertedResults(bool explicitIdentity)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook, automatic: false));
        var options = new RespireStreamAddOptions
        {
            Idempotency = explicitIdentity ? RespireStreamIdempotency.Manual("producer", "message") : null,
        };
        await Assert.That(await client.Streams.AddAsync("s", options, ("f", "v"))).IsEqualTo((RespireStreamId?)"1-0");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        hook.Completed.Clear();
        server.ReplyOverride = (_, command) => command.StartsWith("XADD ", StringComparison.Ordinal)
            ? "-ERR malformed append\r\n"u8.ToArray() : null;
        await Assert.That(async () => await client.Streams.AddAsync("s", options, ("f", "v")))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
    }

    [Test]
    public async Task CancellationBetweenProbeAndFallbackCompletesOnceWithCallerToken()
    {
        await using var server = Server();
        using var canceled = new CancellationTokenSource();
        server.ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith("XADD ", StringComparison.Ordinal)) return null;
            canceled.Cancel();
            return "-ERR Invalid stream ID specified as stream command argument\r\n"u8.ToArray();
        };
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await Assert.That(async () => await Add(client, false, canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(hook.Completed.Single().Context.CancellationToken).IsEqualTo(canceled.Token);
        await Assert.That(AddCount(server)).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FallbackWithoutDeadlineOrWithExpiredDeadlineCompletesOnce(bool timeout)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => !command.StartsWith("XADD ", StringComparison.Ordinal) ? null
            : command.Contains(" IDMP ", StringComparison.Ordinal)
                ? "-ERR Invalid stream ID specified as stream command argument\r\n"u8.ToArray()
                : "$3\r\n1-0\r\n"u8.ToArray();
        if (timeout) server.SuppressReply = command => command.StartsWith("XADD ", StringComparison.Ordinal)
            && !command.Contains(" IDMP ", StringComparison.Ordinal);
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        {
            CommandTimeout = timeout ? TimeSpan.FromMilliseconds(500) : null,
        });
        if (timeout)
        {
            await Assert.That(async () => await Add(client, false)).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
            await Assert.That(hook.Completed.Single().Error is RespireTimeoutException).IsTrue();
        }
        else
        {
            await Assert.That(await Add(client, false)).IsEqualTo((RespireStreamId?)"1-0");
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        }
        await Assert.That(hook.Completed.Single().Context.CancellationToken).IsEqualTo(CancellationToken.None);
        await Assert.That(AddCount(server)).IsEqualTo(2);
    }

    [Test]
    public async Task ReentrantApplicationCommandHasItsOwnScopeAndObserverFailureIsIgnored()
    {
        await using var server = Server();
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        Task<RespireStreamId?>? nested = null;
        var entered = 0;
        hook.Before = _ =>
        {
            if (Interlocked.Increment(ref entered) == 1) nested = Add(client, true);
        };
        await Add(client, false);
        await nested!;
        await Assert.That(entered).IsEqualTo(2);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
        await Assert.That(AddCount(server)).IsEqualTo(2);
    }

    [Test]
    public async Task LostReplyRetryRetainsOneHookScopeAndOneEntry()
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            CommandHook = hook,
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked },
        });
        using var fault = server.InjectFault("XADD", RespireFakeFault.Disconnect(afterExecution: true));
        await Add(client, false).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(fault.ExecutionCount).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(1);
    }

    private static async Task<RespireStreamId?> Add(RespireClient client, bool optional, CancellationToken token = default)
        => optional
            ? await client.Streams.AddAsync("s", new RespireStreamAddOptions { CreateStream = false }, ("f", "v"), token)
            : await client.Streams.AddAsync("s", ("f", "v"), token);

    private static FakeRespServer Server() => new(64, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command.StartsWith("XADD ", StringComparison.Ordinal)
            ? "$3\r\n1-0\r\n"u8.ToArray() : null,
    };

    private static RespireOptions Options(FakeRespServer server, RecordingHook hook, bool automatic = true) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false,
        CommandTimeout = TimeSpan.FromSeconds(3), CommandHook = hook,
        RetryPolicy = automatic ? new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked } : null,
    };

    private static int AddCount(FakeRespServer server)
        => server.ReceivedCommands.Count(c => c.StartsWith("XADD ", StringComparison.Ordinal));

    private sealed class RecordingHook : RespireCommandHook
    {
        public Action<RespireCommandContext>? Before;
        public bool ThrowOnCompleted;
        public readonly ConcurrentQueue<(RespireCommandContext Context, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Before?.Invoke(context);
            return null;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, outcome, error));
            if (ThrowOnCompleted) throw new InvalidOperationException("Ignored observer failure");
        }
    }
}
