using System.Collections.Concurrent;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ImportHealthCommandHookTests
{
    [Test]
    [Arguments("MSET", false)]
    [Arguments("MSETNX", false)]
    [Arguments("MSETEX", false)]
    [Arguments("MSET", true)]
    [Arguments("MSETNX", true)]
    [Arguments("MSETEX", true)]
    public async Task BulkWritesAdmitBeforeSending(string command, bool cancel)
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        using var fault = server.InjectFault(command, RespireFakeFault.Loading());
        using var caller = new CancellationTokenSource();
        if (cancel) caller.Cancel();
        else hook.Before = _ => throw new InvalidOperationException("rejected");
        await Assert.That(async () =>
        {
            (RespireKey, RespireValue)[] pairs = [("first", "one"), ("second", "two")];
            switch (command)
            {
                case "MSET": await client.Strings.SetManyAsync(pairs, caller.Token); break;
                case "MSETNX": await client.Strings.SetManyIfNotExistsAsync(pairs, caller.Token); break;
                case "MSETEX": await client.Strings.SetManyExpireAsync(TimeSpan.FromSeconds(30), pairs, caller.Token); break;
            }
        }).Throws<Exception>();
        await Assert.That(fault.MatchedCount).IsEqualTo(0);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo(command);
        await Assert.That(completion.Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Rejected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SessionAdmissionPrecedesConnectionSetup(bool cancel)
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
        var options = server.CreateOptions();
        var factory = options.TestingStreamFactory!;
        var connections = 0;
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(options with
        {
            CommandHook = hook,
            TestingStreamFactory = (host, port, token) =>
            {
                Interlocked.Increment(ref connections);
                return factory(host, port, token);
            },
        });
#pragma warning restore RESP010
        var before = connections;
        using var caller = new CancellationTokenSource();
        if (cancel) caller.Cancel();
        else hook.Before = _ => throw new InvalidOperationException("rejected");
        await Assert.That(async () => await client.Hashes.CreateImportSessionAsync(caller.Token)).Throws<Exception>();
        await Assert.That(connections).IsEqualTo(before);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo("HIMPORT OPEN");
        await Assert.That(completion.Context.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(completion.Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Rejected);
    }

    [Test]
    [Arguments("HIMPORT PREPARE", false)]
    [Arguments("HIMPORT SET", false)]
    [Arguments("HIMPORT DISCARD", false)]
    [Arguments("HIMPORT DISCARDALL", false)]
    [Arguments("HIMPORT PREPARE", true)]
    [Arguments("HIMPORT SET", true)]
    [Arguments("HIMPORT DISCARD", true)]
    [Arguments("HIMPORT DISCARDALL", true)]
    public async Task RejectedOrCanceledImportPreservesFieldsets(string command, bool cancel)
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This cancellation scenario owns its client, hook state and scripted in-flight reply boundaries.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        hook.Completed.Clear();
        using var fault = server.InjectFault("HIMPORT", RespireFakeFault.Loading());
        using var caller = new CancellationTokenSource();
        if (cancel) caller.Cancel();
        else hook.Before = _ => throw new InvalidOperationException("rejected");
        await Assert.That(async () => await Import(session, command, caller.Token)).Throws<Exception>();
        await Assert.That(fault.MatchedCount).IsEqualTo(0);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo(command);
        await Assert.That(completion.Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Rejected);
        fault.Dispose();
        hook.Before = null;
        await Assert.That(await session.SetAsync("after", "schema", "retained")).IsTrue();
        await Assert.That(await client.Hashes.GetStringAsync("after", "field")).IsEqualTo("retained");
    }

    [Test]
    public async Task ImportsCompleteOnceAfterConversionAndReleaseUsage()
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook { ThrowOnCompleted = true };
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        Task<bool>? followup = null;
        hook.After = context =>
        {
            if (context.Command == "HIMPORT SET") followup = session.DiscardAsync("schema").AsTask();
        };
        await session.SetAsync("key", "schema", "value");
        await Assert.That(await followup!).IsTrue();
        await Assert.That(await session.DiscardAllAsync()).IsEqualTo(0);
        await session.DisposeAsync();
        await session.DisposeAsync();
        await Assert.That(hook.Completed.Select(c => c.Context.Command).Order()).IsEquivalentTo(
            new[] { "HIMPORT OPEN", "HIMPORT PREPARE", "HIMPORT SET", "HIMPORT DISCARD", "HIMPORT DISCARDALL" }.Order());
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    [Test]
    public async Task DefinitiveImportErrorKeepsSessionAndReportsOriginalError()
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        await using var session = await client.Hashes.CreateImportSessionAsync();
        hook.Completed.Clear();
        var error = await Assert.That(async () => await session.SetAsync("key", "missing", "value"))
            .Throws<RespireServerException>();
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await session.PrepareAsync("schema", "field");
        await session.SetAsync("after", "schema", "value");
    }

    [Test]
    public async Task ImportCancellationAfterSendingExpiresBeforeCompletion()
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This cancellation scenario owns its client, hook state and scripted in-flight reply boundaries.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        hook.Completed.Clear();
        using var caller = new CancellationTokenSource();
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("HIMPORT", RespireFakeFault.Pause(gate, afterExecution: true));
        var pending = session.SetAsync("key", "schema", ["value"], caller.Token).AsTask();
        try
        {
            await fault.Matched.WaitAsync(TimeSpan.FromSeconds(10));
            caller.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
                .Throws<OperationCanceledException>();
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
            await Assert.That(() => session.CreateBatch()).Throws<ObjectDisposedException>();
        }
        finally { gate.Release(); }
    }

    [Test]
    public async Task MalformedImportConversionFailsInsideScopeAndExpiresSession()
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            Connections = 1, ThreadPoolMonitoring = false, CommandHook = hook,
        });
#pragma warning restore RESP010
        await using var session = await client.Hashes.CreateImportSessionAsync();
        hook.Completed.Clear();
        var error = await Assert.That(async () => await session.DiscardAllAsync()).Throws<RespireProtocolException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        await Assert.That(() => session.CreateBatch()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task UncertainImportExpiresBeforeCompletionAndCleanupCannotBeRejected()
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        hook.Completed.Clear();
        var expiredAtCompletion = false;
        hook.After = _ =>
        {
            try { session.CreateBatch(); }
            catch (ObjectDisposedException) { expiredAtCompletion = true; }
        };
        using var fault = server.InjectFault("HIMPORT", RespireFakeFault.Disconnect(afterExecution: true));
        await Assert.That(async () => await session.SetAsync("key", "schema", "value")).Throws<Exception>();
        await Assert.That(expiredAtCompletion).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        hook.Before = _ => throw new InvalidOperationException("cleanup must not admit");
        await session.DisposeAsync();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HealthAdmissionRejectsOrCancelsBeforePing(bool cancel)
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        using var fault = server.InjectFault("PING", RespireFakeFault.Loading());
        using var caller = new CancellationTokenSource();
        if (cancel) caller.Cancel();
        else hook.Before = _ => throw new InvalidOperationException("rejected");
        await Assert.That(async () => await client.ProbeHealthAsync(cancellationToken: caller.Token)).Throws<Exception>();
        await Assert.That(fault.MatchedCount).IsEqualTo(0);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo("PROBE HEALTH");
        await Assert.That(completion.Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Rejected);
    }

    [Test]
    public async Task HealthMixedResultsSucceedAndNestedApplicationCallsHaveIndependentScopes()
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook { ThrowOnCompleted = true };
#pragma warning disable RESP010 // This import/health hook scenario owns its client and fault-injected peer state.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            CommandHook = hook, ReplicaEndpoints = [new("unused-replica")],
        });
#pragma warning restore RESP010
        Task? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "PROBE HEALTH") nested = client.Strings.SetAsync("nested", "value").AsTask();
        };
        var results = await ((IRespireHealthProbe)client.WithKeyPrefix("tenant:")).ProbeHealthAsync(new() { ProbeAllNodes = true });
        await nested!;
        await Assert.That(results.Length).IsEqualTo(2);
        await Assert.That(results[0].Error).IsNull();
        await Assert.That(results[1].Error is RespireConnectionException).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Any(c => c.Context.Command == "PING")).IsFalse();
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HealthNodeErrorIsDataButCallerCancellationCancelsScope(bool cancel)
    {
        await using var server = new RespireFakeServer();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This cancellation scenario owns its client, hook state and scripted in-flight reply boundaries.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { CommandHook = hook });
#pragma warning restore RESP010
        using var caller = new CancellationTokenSource();
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("PING", cancel ? RespireFakeFault.Pause(gate) : RespireFakeFault.Loading());
        var task = client.ProbeHealthAsync(cancellationToken: caller.Token).AsTask();
        try
        {
            if (cancel)
            {
                await fault.Matched.WaitAsync(TimeSpan.FromSeconds(10));
                caller.Cancel();
                await Assert.That(async () => await task.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
            }
            else
            {
                var results = await task;
                await Assert.That(results.Single().Error is RespireServerException).IsTrue();
            }
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Succeeded);
        }
        finally { gate.Release(); }
    }

    private static async Task Import(RespireHashImportSession session, string command, CancellationToken token)
    {
        switch (command)
        {
            case "HIMPORT PREPARE": await session.PrepareAsync("schema", ["changed"], token); break;
            case "HIMPORT SET": await session.SetAsync("key", "schema", ["value"], token); break;
            case "HIMPORT DISCARD": await session.DiscardAsync("schema", token); break;
            case "HIMPORT DISCARDALL": await session.DiscardAllAsync(token); break;
        }
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        public Action<RespireCommandContext>? Before { get; set; }
        public Action<RespireCommandContext>? After { get; set; }
        public bool ThrowOnCompleted { get; init; }
        public ConcurrentQueue<(RespireCommandContext Context, RespireCommandOutcome Outcome, Exception? Error)> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context) { Before?.Invoke(context); return context; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, outcome, error));
            After?.Invoke(context);
            if (ThrowOnCompleted) throw new InvalidOperationException("ignored observer error");
        }
    }
}
