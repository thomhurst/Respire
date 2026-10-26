using System.Collections.Concurrent;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PipelineHookTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AdmissionRunsBeforeWireAndCompletionAfterPendingResults(bool transaction)
    {
        await using var server = Server(transaction);
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        var baseline = server.CommandsSeen;
        hook.Before = context =>
        {
            if (server.CommandsSeen != baseline) throw new InvalidOperationException("Sent before admission.");
            if (context.Commands.Count != 1 || context.Commands[0] != "GET") throw new InvalidOperationException("Wrong snapshot.");
        };
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            var pending = tx.GetString("key");
            hook.After = () => hook.PendingCompleted = pending.HasResult;
            await tx.CommitAsync();
            await Assert.That(pending.Result).IsEqualTo("value");
        }
        else
        {
            using var batch = client.CreateBatch();
            var pending = batch.GetString("key");
            hook.After = () => hook.PendingCompleted = pending.HasResult;
            await batch.ExecuteAsync();
            await Assert.That(pending.Result).IsEqualTo("value");
        }
        await Assert.That(hook.PendingCompleted).IsTrue();
        await Assert.That(hook.Context!.IsTransaction).IsEqualTo(transaction);
        await Assert.That(hook.Entries).IsEqualTo(1);
        await Assert.That(hook.Completions).IsEqualTo(1);
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Succeeded);
        await Assert.That(ReferenceEquals(hook.State, hook.CompletedState)).IsTrue();
        // Snapshot is immutable even through the collection's mutable interface.
        await Assert.That(() => ((IList<string>)hook.Context.Commands)[0] = "SET").Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectionFailsEveryPendingAndSendsNothing(bool transaction)
    {
        await using var server = Server(transaction);
        var rejected = new InvalidOperationException("Policy rejected GET.");
        var hook = new RecordingHook { Before = _ => throw rejected };
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        var baseline = server.CommandsSeen;
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            var pending = tx.GetString("key");
            var error = await Failure(async () => await tx.CommitAsync());
            await Assert.That(ReferenceEquals(error, rejected)).IsTrue();
            await Assert.That(ReferenceEquals(await Failure(() => Task.FromResult(pending.Result)), rejected)).IsTrue();
        }
        else
        {
            using var batch = client.CreateBatch();
            var first = batch.GetString("key");
            var second = batch.GetString("other");
            var result = await batch.TryExecuteAsync();
            await Assert.That(result.FailureCount).IsEqualTo(2);
            await Assert.That(ReferenceEquals(result.FirstError, rejected)).IsTrue();
            await Assert.That(ReferenceEquals(await Failure(() => Task.FromResult(first.Result)), rejected)).IsTrue();
            await Assert.That(ReferenceEquals(await Failure(() => Task.FromResult(second.Result)), rejected)).IsTrue();
            await Assert.That(async () => await batch.TryExecuteAsync()).Throws<InvalidOperationException>();
        }
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Entries).IsEqualTo(1);
        await Assert.That(hook.Completions).IsEqualTo(1);
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Rejected);
        await Assert.That(hook.CompletedState).IsNull();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FinalErrorsAndCancellationAreObservedWithoutReplacingOutcomes(bool transaction, bool cancel)
    {
        await using var server = Server(transaction, fail: true);
        var hook = new RecordingHook { After = () => throw new InvalidOperationException("Observer failed.") };
#pragma warning disable RESP010 // This cancellation scenario owns its client, hook state and scripted in-flight reply boundaries.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        using var cts = new CancellationTokenSource();
        if (cancel) cts.Cancel();
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            var pending = tx.GetString("key");
            if (cancel) await Failure(async () => await tx.CommitAsync(cts.Token));
            else await tx.CommitAsync(cts.Token); // EXEC member errors belong to their pending results.
            await Failure(() => Task.FromResult(pending.Result));
        }
        else
        {
            using var batch = client.CreateBatch();
            _ = batch.GetString("key");
            var result = await batch.TryExecuteAsync(cts.Token);
            await Assert.That(result.FailureCount).IsEqualTo(1);
            await Assert.That(ReferenceEquals(result.FirstError, hook.Error)).IsTrue();
        }
        await Assert.That(hook.Outcome).IsEqualTo(cancel ? RespirePipelineOutcome.Canceled : RespirePipelineOutcome.Failed);
        await Assert.That(cancel ? hook.Error is OperationCanceledException : hook.Error is RespireServerException).IsTrue();
        await Assert.That(hook.Context!.CancellationToken).IsEqualTo(cts.Token);
        await Assert.That(hook.Completions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyExecutionsAndObserverExceptionsPreserveSuccess(bool transaction)
    {
        var hook = new RecordingHook { After = () => throw new Exception("ignored") };
        using var endpoint = new ReservedUnavailablePort();
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = RespireClient.Create(new RespireOptions
        { Endpoints = [new("127.0.0.1", endpoint.Port)], PipelineHook = hook, ThreadPoolMonitoring = false });
#pragma warning restore RESP010
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            await tx.CommitAsync();
        }
        else
        {
            using var batch = client.CreateBatch();
            await batch.ExecuteAsync();
        }
        await Assert.That(hook.Context!.Commands.Count).IsEqualTo(0);
        await Assert.That(hook.Completions).IsEqualTo(1);
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Succeeded);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DurabilityIncludesAcknowledgementAndCanRejectItBeforeWrites(bool aof)
    {
        await using var server = Server(false);
        var hook = new RecordingHook { Before = _ => throw new InvalidOperationException("No WAIT") };
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        var baseline = server.CommandsSeen;
        using var batch = client.CreateBatch();
        var pending = batch.Set("key", "value");
        await Failure(async () =>
        {
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero);
        });
        await Assert.That(hook.Context!.Commands.SequenceEqual(new[] { "SET", aof ? "WAITAOF" : "WAIT" })).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Rejected);
        await Assert.That(ReferenceEquals(await Failure(() => Task.FromResult(pending.Result)), hook.Error)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DurabilityCompletesAfterAcknowledgement(bool aof)
    {
        await using var server = Server(false);
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        _ = batch.Set("key", "value");
        if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero);
        else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero);
        await Assert.That(server.ReceivedCommands.Any(c => c.StartsWith(aof ? "WAITAOF " : "WAIT "))).IsTrue();
        await Assert.That(hook.Completions).IsEqualTo(1);
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Succeeded);
    }

    [Test]
    public async Task PipelineRetriesHaveOneAdmissionAndOneCompletion()
    {
        var reads = 0;
        await using var server = Server(false);
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray()
            : command == "CLIENT ID" ? ":1\r\n"u8.ToArray() : null;
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This retry/fallback scenario owns its client and scripted attempt counters across one logical call.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        var pending = batch.GetString("key");
        await batch.ExecuteAsync();
        await Assert.That(pending.Result).IsEqualTo("value");
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(hook.Entries).IsEqualTo(1);
        await Assert.That(hook.Completions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WatchedAbortAndRejectedCommitAreDistinct(bool reject)
    {
        await using var server = Server(true);
        server.ReplyOverride = (_, command) => command switch
        {
            "EXEC" => "*-1\r\n"u8.ToArray(),
            "GET key" => "+QUEUED\r\n"u8.ToArray(),
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            _ => null,
        };
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        await using var tx = await client.CreateTransactionAsync(["key"]);
        await Assert.That(hook.Entries).IsEqualTo(0); // WATCH setup is outside the commit.
        _ = tx.GetString("key");
        if (reject)
        {
            hook.Before = _ => throw new InvalidOperationException("No commit");
            await Failure(async () => await tx.CommitAsync());
            await Assert.That(server.ReceivedCommands.Contains("MULTI")).IsFalse();
        }
        else await Assert.That(await tx.CommitAsync()).IsFalse();
        await Assert.That(hook.Outcome).IsEqualTo(reject ? RespirePipelineOutcome.Rejected : RespirePipelineOutcome.Aborted);
        await Assert.That(hook.Completions).IsEqualTo(1);
    }

    [Test]
    public async Task TransactionRetriesHaveOneAdmissionAndOneCompletion()
    {
        var execs = 0;
        var transactions = new ConcurrentDictionary<int, bool>();
        await using var server = Server(true);
        server.ReplyOverride = (connection, command) =>
        {
            if (command == "MULTI") { transactions[connection] = true; return FakeRespServer.OkReply; }
            if (command == "EXEC")
            {
                transactions[connection] = false;
                if (Interlocked.Increment(ref execs) == 1) { server.CloseConnections(); return null; }
                return "*1\r\n$5\r\nvalue\r\n"u8.ToArray();
            }
            if (transactions.GetValueOrDefault(connection)) return "+QUEUED\r\n"u8.ToArray();
            return command == "CLIENT ID" ? ":1\r\n"u8.ToArray() : null;
        };
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This retry/fallback scenario owns its client and scripted attempt counters across one logical call.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        await using var tx = client.CreateTransaction();
        var pending = tx.GetString("key");
        await tx.CommitAsync();
        await Assert.That(pending.Result).IsEqualTo("value");
        await Assert.That(execs).IsEqualTo(2);
        await Assert.That(hook.Entries).IsEqualTo(1);
        await Assert.That(hook.Completions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DurabilityFailurePreservesCompletedWrite(bool aof)
    {
        await using var server = Server(false);
        server.ReplyOverride = (_, command) => command.StartsWith("WAIT") ? "-ERR acknowledgement failed\r\n"u8.ToArray() : null;
        var hook = new RecordingHook { After = () => throw new Exception("ignored") };
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        var pending = batch.Set("key", "value");
        var error = await Failure(async () =>
        {
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero);
        });
        await Assert.That(pending.Result).IsTrue();
        await Assert.That(ReferenceEquals(hook.Error, error)).IsTrue();
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Failed);
    }

    [Test]
    public async Task ClientViewsShareHookButIndividualCommandsDoNotInvokeIt()
    {
        await using var server = Server(false);
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        await client.GetStringAsync("key");
        await Assert.That(hook.Entries).IsEqualTo(0);
        using var batch = client.WithKeyPrefix("prefix:").CreateBatch();
        _ = batch.GetString("key");
        await batch.ExecuteAsync();
        await Assert.That(hook.Entries).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Contains("GET prefix:key")).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ImportSessionIsReusableAfterCompletionAndAdmissionRejection(bool transaction, bool reject)
    {
        await using var server = Server(transaction);
        var inTransaction = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command == "MULTI") { inTransaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inTransaction = false; return "*1\r\n+OK\r\n"u8.ToArray(); }
            return inTransaction ? "+QUEUED\r\n"u8.ToArray() : null;
        };
        var hook = new RecordingHook();
        if (reject) hook.Before = _ => throw new InvalidOperationException("Rejected import");
#pragma warning disable RESP010 // This pipeline hook scenario owns its client, callback state and scripted aggregate replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        var outsideGate = false;
        hook.After = () =>
        {
            using var usage = session.EnterOperation();
            outsideGate = true;
        };
        if (transaction)
        {
            await using var tx = session.CreateTransaction();
            _ = tx.Hashes.Import("key", "schema", "value");
            if (reject) await Failure(async () => await tx.CommitAsync());
            else await tx.CommitAsync();
        }
        else
        {
            using var batch = session.CreateBatch();
            _ = batch.Hashes.Import("key", "schema", "value");
            if (reject) await Failure(async () => await batch.ExecuteAsync());
            else await batch.ExecuteAsync();
        }
        await Assert.That(outsideGate).IsTrue();
        await Assert.That(hook.Outcome).IsEqualTo(reject ? RespirePipelineOutcome.Rejected : RespirePipelineOutcome.Succeeded);
        await Assert.That(await session.SetAsync("later", "schema", "value")).IsTrue();
    }

    [Test]
    public async Task EmptyRejectionThrowsAndInvalidLifecycleDoesNotInvokeHookAgain()
    {
        var hook = new RecordingHook { Before = _ => throw new InvalidOperationException("No execution") };
        using var endpoint = new ReservedUnavailablePort();
#pragma warning disable RESP010 // This preflight scenario owns isolated client options and hooks to verify rejection before I/O.
        await using var client = RespireClient.Create(new RespireOptions
        { Endpoints = [new("127.0.0.1", endpoint.Port)], PipelineHook = hook, ThreadPoolMonitoring = false });
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        await Assert.That(async () => await batch.TryExecuteAsync()).Throws<InvalidOperationException>();
        await Assert.That(async () => await batch.TryExecuteAsync()).Throws<InvalidOperationException>();
        await Assert.That(hook.Entries).IsEqualTo(1);
        await Assert.That(hook.Completions).IsEqualTo(1);
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Rejected);
    }

    [Test]
    public async Task ConcurrentExecutionsRetainTheirOwnCallbackState()
    {
        await using var server = Server(false);
        var hook = new ConcurrentHook();
#pragma warning disable RESP010 // This reentrancy/concurrency scenario owns its client and shared callback state until all calls finish.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            using var batch = client.CreateBatch();
            var pending = batch.GetString("key" + index);
            await batch.ExecuteAsync();
            await Assert.That(pending.Result).IsEqualTo("value");
        }));
        await Assert.That(hook.Completed).IsEqualTo(16);
        await Assert.That(hook.Active.IsEmpty).IsTrue();
        await Assert.That(hook.Errors.IsEmpty).IsTrue();
    }

    private sealed class ConcurrentHook : RespirePipelineHook
    {
        internal readonly ConcurrentDictionary<RespirePipelineContext, object> Active = new();
        internal readonly ConcurrentQueue<string> Errors = new();
        internal int Completed;

        public override object? OnExecuting(RespirePipelineContext context)
        {
            var state = new object();
            if (!Active.TryAdd(context, state)) Errors.Enqueue("Repeated context");
            return state;
        }

        public override void OnCompleted(RespirePipelineContext context, object? state, RespirePipelineOutcome outcome, Exception? error)
        {
            if (!Active.TryRemove(context, out var expected) || !ReferenceEquals(state, expected))
                Errors.Enqueue("Mismatched state");
            if (outcome != RespirePipelineOutcome.Succeeded || error is not null) Errors.Enqueue("Unexpected failure");
            Interlocked.Increment(ref Completed);
        }
    }

    private static FakeRespServer Server(bool transaction, bool fail = false)
    {
        var transactions = new ConcurrentDictionary<int, bool>();
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        server.ReplyOverride = (connection, command) =>
        {
            if (command == "CLIENT ID") return ":1\r\n"u8.ToArray();
            if (transaction && command == "MULTI") { transactions[connection] = true; return FakeRespServer.OkReply; }
            if (transaction && command == "EXEC")
            {
                transactions[connection] = false;
                return fail ? "*1\r\n-ERR failed\r\n"u8.ToArray() : "*1\r\n$5\r\nvalue\r\n"u8.ToArray();
            }
            if (transactions.GetValueOrDefault(connection)) return "+QUEUED\r\n"u8.ToArray();
            if (command.StartsWith("GET ")) return fail ? "-ERR failed\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray();
            if (command.StartsWith("WAITAOF ")) return "*2\r\n:1\r\n:1\r\n"u8.ToArray();
            if (command.StartsWith("WAIT ")) return ":1\r\n"u8.ToArray();
            return null;
        };
        return server;
    }

    private static RespireOptions Options(FakeRespServer server, RespirePipelineHook hook) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(3), PipelineHook = hook,
    };

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private sealed class RecordingHook : RespirePipelineHook
    {
        internal Action<RespirePipelineContext>? Before;
        internal Action? After;
        internal readonly object State = new();
        internal object? CompletedState;
        internal RespirePipelineContext? Context;
        internal RespirePipelineOutcome Outcome;
        internal Exception? Error;
        internal int Entries;
        internal int Completions;
        internal bool PendingCompleted;
        public override object? OnExecuting(RespirePipelineContext context)
        {
            Context = context;
            Entries++;
            Before?.Invoke(context);
            return State;
        }
        public override void OnCompleted(RespirePipelineContext context, object? state, RespirePipelineOutcome outcome, Exception? error)
        {
            Completions++;
            CompletedState = state;
            Outcome = outcome;
            Error = error;
            After?.Invoke();
        }
    }
}
