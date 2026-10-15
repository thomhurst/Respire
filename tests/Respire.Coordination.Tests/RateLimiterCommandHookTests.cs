using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class RateLimiterCommandHookTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectionPrecedesAcquisitionAndSettingsChanges(bool composite)
    {
        await using var server = Server(composite);
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        await using var limiter = Create(client, composite);
        hook.Before = _ => throw new InvalidOperationException("Denied");
        await Assert.That(async () => await limiter.AcquireAsync()).Throws<InvalidOperationException>();
        await Assert.That(async () => await Update(client, limiter, composite)).Throws<InvalidOperationException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        await Assert.That(limiter.GetStatistics()!.CurrentQueuedCount).IsEqualTo(0);
        await Assert.That(limiter.GetStatistics()!.TotalFailedLeases).IsEqualTo(0);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Rejected)).IsTrue();
        hook.Before = null;
        // Rejection did not publish the larger limit.
        using var oversized = await limiter.AcquireAsync(2);
        await Assert.That(oversized.IsAcquired).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScriptFallbackAndFinalLeaseHaveOneCompletion(bool composite)
    {
        await using var server = Server(composite);
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        await using var limiter = Create(client, composite);
        var calls = 0;
        var original = server.ReplyOverride;
        server.ReplyOverride = (connection, command) =>
            !composite && Interlocked.Increment(ref calls) == 1
                ? "-NOSCRIPT missing\r\n"u8.ToArray() : original!(connection, command);
        using var lease = await limiter.AcquireAsync();
        await Assert.That(lease.IsAcquired).IsTrue();
        await Assert.That(hook.Completed.Single()).IsEqualTo(("RATE LIMIT ACQUIRE", RespireCommandOutcome.Succeeded));
        await Assert.That(server.CommandsSeen).IsEqualTo(composite ? 1 : 2);
        await Assert.That(limiter.GetStatistics()!.TotalSuccessfulLeases).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MalformedLeaseReplyFailsOuterOperation(bool composite)
    {
        await using var server = new FakeRespServer("*1\r\n:1\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        await using var limiter = Create(client, composite);
        await Assert.That(async () => await limiter.AcquireAsync()).Throws<RespireProtocolException>();
        await Assert.That(hook.Completed.Single()).IsEqualTo(("RATE LIMIT ACQUIRE", RespireCommandOutcome.Failed));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UpdatesCompleteOnceAfterPublishingSettings(bool composite)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        await using var limiter = Create(client, composite);
        await Update(client, limiter, composite);
        await Assert.That(hook.Completed.Single()).IsEqualTo(("RATE LIMIT UPDATE", RespireCommandOutcome.Succeeded));
        server.ReplyOverride = (_, _) => Reply(composite, granted: true);
        using var lease = await limiter.AcquireAsync(2);
        await Assert.That(lease.IsAcquired).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task LocalAdoptionAndNoOpUpdatesStillHaveScopes()
    {
        await using var server = Server(false);
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        await using var limiter = Create(client, false);
        var settings = RespireRateLimit.FixedWindow(2, TimeSpan.FromSeconds(1));
        await client.Coordination.RateLimiters.UpdateAsync(limiter, settings, RespireRateLimitUpdateMode.Adopt);
        await client.Coordination.RateLimiters.UpdateAsync(limiter, settings);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c == ("RATE LIMIT UPDATE", RespireCommandOutcome.Succeeded))).IsTrue();
    }

    [Test]
    public async Task CachedDenialAndRejectedWritePreserveCacheAndUsePublicScopes()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var list = new RespireRateLimitDenyList(client, "deny", TimeSpan.FromMinutes(1));
        await Assert.That(await list.IsDeniedAsync("user")).IsTrue();
        hook.Before = _ => throw new InvalidOperationException("Denied");
        await Assert.That(async () => await list.IsDeniedAsync("user")).Throws<InvalidOperationException>();
        await Assert.That(async () => await list.SetDeniedAsync("user", false)).Throws<InvalidOperationException>();
        hook.Before = null;
        await Assert.That(await list.IsDeniedAsync("user")).IsTrue();
        await using var limiter = client.Coordination.RateLimiters.WithDenyList(list, "user")
            .FixedWindow("limit", 1, TimeSpan.FromSeconds(1));
        using var lease = await limiter.AcquireAsync();
        await Assert.That(lease.TryGetMetadata(MetadataName.ReasonPhrase, out string? reason)).IsTrue();
        await Assert.That(reason).IsEqualTo("deny_list");
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await Assert.That(hook.Completed.Count).IsEqualTo(5);
        await Assert.That(hook.Completed.Last()).IsEqualTo(("RATE LIMIT ACQUIRE", RespireCommandOutcome.Succeeded));
        await list.SetDeniedAsync("user", false);
        await Assert.That(hook.Completed.Last()).IsEqualTo(("RATE LIMIT DENY LIST WRITE", RespireCommandOutcome.Succeeded));
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DifferentDenyListClientRetainsAdmissionEvenForCachedReads(bool cached)
    {
        await using var listServer = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var limiterServer = Server(false);
        var listHook = new RecordingHook();
        var limiterHook = new RecordingHook();
        await using var listClient = await Connect(listServer, listHook);
        await using var limiterClient = await Connect(limiterServer, limiterHook);
        var list = new RespireRateLimitDenyList(listClient, "deny", TimeSpan.FromMinutes(1));
        if (cached) await list.IsDeniedAsync("user");
        listHook.Completed.Clear();
        await using var limiter = limiterClient.Coordination.RateLimiters.WithDenyList(list, "user")
            .FixedWindow("limit", 1, TimeSpan.FromSeconds(1));

        listHook.Before = _ => throw new InvalidOperationException("Denied by list client");
        await Assert.That(async () => await limiter.AcquireAsync()).Throws<InvalidOperationException>();
        await Assert.That(listServer.CommandsSeen).IsEqualTo(cached ? 1 : 0);
        await Assert.That(limiterServer.CommandsSeen).IsEqualTo(0);
        await Assert.That(listHook.Completed.Single()).IsEqualTo(("RATE LIMIT DENY LIST READ", RespireCommandOutcome.Rejected));
        await Assert.That(limiterHook.Completed.Single()).IsEqualTo(("RATE LIMIT ACQUIRE", RespireCommandOutcome.Failed));

        listHook.Before = null;
        using var lease = await limiter.AcquireAsync();
        await Assert.That(lease.IsAcquired).IsFalse();
        await Assert.That(listHook.Completed.Last()).IsEqualTo(("RATE LIMIT DENY LIST READ", RespireCommandOutcome.Succeeded));
        await Assert.That(limiterHook.Completed.Last()).IsEqualTo(("RATE LIMIT ACQUIRE", RespireCommandOutcome.Succeeded));
        await Assert.That(listServer.CommandsSeen).IsEqualTo(1);
        await Assert.That(limiterServer.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task CoalescedReadCancellationCompletesEachCallerIndependently()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var list = new RespireRateLimitDenyList(client, "deny", TimeSpan.FromMinutes(1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.GateCommand("EVALSHA ", gate.Task);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var first = list.IsDeniedAsync("user", cancellation.Token).AsTask();
            var second = list.IsDeniedAsync("user").AsTask();
            await cancellation.CancelAsync();
            await Assert.That(async () => await first).Throws<OperationCanceledException>();
            await Assert.That(second.IsCompleted).IsFalse();
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            gate.TrySetResult();
            await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
            await Assert.That(hook.Completed.Count).IsEqualTo(2);
            await Assert.That(server.CommandsSeen).IsEqualTo(1);
        }
        finally { gate.TrySetResult(); }
    }

    [Test]
    [Arguments("success")]
    [Arguments("cancel")]
    [Arguments("dispose")]
    public async Task QueuedRetriesKeepOneScopePerCaller(string outcome)
    {
        await using var server = Server(false);
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        await using var limiter = Create(client, false);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        server.ReplyOverride = (_, _) =>
        {
            var count = Interlocked.Increment(ref attempts);
            if (count > 1) attempted.TrySetResult();
            return Reply(false, outcome == "success" && count > 1);
        };
        using var cancellation = new CancellationTokenSource();
        var pending = limiter.AcquireAsync(1, cancellation.Token).AsTask();
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (outcome == "cancel") await cancellation.CancelAsync();
        if (outcome == "dispose") await limiter.DisposeAsync();
        if (outcome == "success")
        {
            using var lease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(lease.IsAcquired).IsTrue();
        }
        else if (outcome == "cancel")
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        else
            await Assert.That(async () => await pending).Throws<ObjectDisposedException>();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(outcome switch
        {
            "cancel" => RespireCommandOutcome.Canceled,
            "dispose" => RespireCommandOutcome.Failed,
            _ => RespireCommandOutcome.Succeeded,
        });
    }

    [Test]
    public async Task ApplicationReentrancyHasIndependentAdmissionAndObserverErrorsAreIgnored()
    {
        await using var server = Server(false);
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await Connect(server, hook);
        await using var limiter = Create(client, false);
        Task<bool>? independent = null;
        var list = new RespireRateLimitDenyList(client, "deny");
        var original = server.ReplyOverride;
        server.ReplyOverride = (connection, command) => command.Contains(" deny ", StringComparison.Ordinal)
            ? ":0\r\n"u8.ToArray() : original!(connection, command);
        hook.Before = context =>
        {
            if (context.Command == "RATE LIMIT ACQUIRE") independent = list.IsDeniedAsync("user").AsTask();
        };
        using var lease = await limiter.AcquireAsync();
        await independent!;
        await Assert.That(lease.IsAcquired).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Any(c => c.Command == "RATE LIMIT DENY LIST READ")).IsTrue();
    }

    private static RateLimiter Create(RespireClient client, bool composite)
        => composite ? client.Coordination.RateLimiters.Composite("limit", [RespireRateLimit.FixedWindow(1, TimeSpan.FromSeconds(1))], queueLimit: 4)
            : client.Coordination.RateLimiters.FixedWindow("limit", 1, TimeSpan.FromSeconds(1), queueLimit: 4);

    private static ValueTask Update(RespireClient client, RateLimiter limiter, bool composite)
        => composite ? client.Coordination.RateLimiters.UpdateAsync(limiter, [RespireRateLimit.FixedWindow(2, TimeSpan.FromSeconds(1))])
            : client.Coordination.RateLimiters.UpdateAsync(limiter, RespireRateLimit.FixedWindow(2, TimeSpan.FromSeconds(1)));

    private static byte[] Reply(bool composite, bool granted)
        => (composite, granted) switch
        {
            (true, true) => "*3\r\n:1\r\n:0\r\n:0\r\n"u8.ToArray(),
            (true, false) => "*3\r\n:0\r\n:1\r\n:0\r\n"u8.ToArray(),
            (false, true) => "*4\r\n:1\r\n:0\r\n:0\r\n:1\r\n"u8.ToArray(),
            _ => "*4\r\n:0\r\n:1\r\n:0\r\n:1\r\n"u8.ToArray(),
        };

    private static FakeRespServer Server(bool composite)
        => new(Reply(composite, true)) { ReplyOverride = (_, _) => Reply(composite, true) };

    private static ValueTask<RespireClient> Connect(FakeRespServer server, RecordingHook hook)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            CommandHook = hook, PipelineHook = new RejectInternalPipeline(), ThreadPoolMonitoring = false,
        });

    private sealed class RejectInternalPipeline : RespirePipelineHook
    {
        public override object? OnExecuting(RespirePipelineContext context)
            => throw new InvalidOperationException("Internal scripts must not admit a pipeline.");
        public override void OnCompleted(RespirePipelineContext context, object? state,
            RespirePipelineOutcome outcome, Exception? error) { }
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal bool ThrowOnCompleted;
        internal ConcurrentQueue<(string Command, RespireCommandOutcome Outcome)> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context) { Before?.Invoke(context); return null; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context.Command, outcome));
            if (ThrowOnCompleted) throw new InvalidOperationException("Observer failed");
        }
    }
}
