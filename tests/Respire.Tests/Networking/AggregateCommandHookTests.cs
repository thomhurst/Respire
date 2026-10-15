using System.Collections.Concurrent;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AggregateCommandHookTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AllMembersAdmitBeforeWireAndCompleteAfterConvertedResults(bool transaction, bool pipeline)
    {
        await using var server = Server();
        var hook = new Hook();
        var outer = pipeline ? new Pipeline(hook.Events) : null;
        await using var client = await RespireClient.ConnectAsync(Options(server, hook, outer));
        var baseline = server.CommandsSeen;
        hook.Before = _ =>
        {
            if (server.CommandsSeen != baseline) throw new Exception("Sent before all admission finished");
        };
        RespirePending<string?> first;
        RespirePending<long> second;
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            first = tx.GetString("key");
            second = tx.Strings.Length("key");
            hook.After = () => hook.ResultsReady = first.HasResult && second.HasResult;
            await tx.CommitAsync();
        }
        else
        {
            using var batch = client.CreateBatch();
            first = batch.GetString("key");
            second = batch.Strings.Length("key");
            hook.After = () => hook.ResultsReady = first.HasResult && second.HasResult;
            await batch.ExecuteAsync();
        }
        await Assert.That(first.Result).IsEqualTo("value");
        await Assert.That(second.Result).IsEqualTo(5L);
        await Assert.That(hook.ResultsReady).IsTrue();
        var expected = new[] { "enter GET", "enter STRLEN", "complete GET", "complete STRLEN" };
        if (pipeline) expected = ["pipeline enter", .. expected, "pipeline complete"];
        await Assert.That(hook.Events.SequenceEqual(expected)).IsTrue();
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded && c.Error is null
            && ReferenceEquals(c.Context, c.State))).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RejectionFailsEveryPendingAndStopsAdmissionBeforeAnySend(bool transaction, bool rejectPipeline)
    {
        await using var server = Server();
        var error = new OperationCanceledException("Rejected by policy");
        var hook = new Hook { Before = c => { if (c.Command == "STRLEN") throw error; } };
        var outer = new Pipeline(hook.Events) { Rejection = rejectPipeline ? error : null };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook, outer));
        var baseline = server.CommandsSeen;
        RespirePending<string?> first;
        RespirePending<long> second;
        RespirePending<string?> third;
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            first = tx.GetString("key");
            second = tx.Strings.Length("key");
            third = tx.GetString("later");
            await Failure(async () => await tx.CommitAsync());
        }
        else
        {
            using var batch = client.CreateBatch();
            first = batch.GetString("key");
            second = batch.Strings.Length("key");
            third = batch.GetString("later");
            var result = await batch.TryExecuteAsync();
            await Assert.That(result.FailureCount).IsEqualTo(3);
        }
        await Assert.That(ReferenceEquals(first.Error, error) && ReferenceEquals(second.Error, error)
            && ReferenceEquals(third.Error, error)).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Completed.Count).IsEqualTo(rejectPipeline ? 0 : 2);
        if (!rejectPipeline)
        {
            await Assert.That(hook.Completed[0].Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            await Assert.That(hook.Completed[1].Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
            await Assert.That(hook.Completed[1].State).IsNull();
        }
        await Assert.That(outer.Outcome).IsEqualTo(rejectPipeline ? RespirePipelineOutcome.Rejected : RespirePipelineOutcome.Canceled);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    public async Task MemberFailureOrCancellationIsFinalAndObserverErrorsAreIgnored(bool transaction, bool cancel, bool serverError)
    {
        await using var server = Server(failRead: serverError);
        var hook = new Hook { After = () => throw new Exception("ignored observer") };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var cts = new CancellationTokenSource();
        if (cancel) cts.Cancel();
        RespirePending<int> pending;
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            pending = tx.Get<int>("key");
            if (cancel) await Failure(async () => await tx.CommitAsync(cts.Token));
            else await tx.CommitAsync(cts.Token);
        }
        else
        {
            using var batch = client.CreateBatch();
            pending = batch.Get<int>("key");
            var result = await batch.TryExecuteAsync(cts.Token);
            await Assert.That(result.FailureCount).IsEqualTo(1);
        }
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed[0].Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Failed);
        await Assert.That(ReferenceEquals(pending.Error, hook.Completed[0].Error)).IsTrue();
        await Assert.That(hook.Completed[0].Context.CancellationToken).IsEqualTo(cts.Token);
        await Assert.That(pending.Error).IsNotNull();
        if (serverError) await Assert.That(pending.Error is RespireServerException).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyAggregatesHaveNoMemberCallbacksAndInvalidReuseDoesNotReadmit(bool transaction)
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            await tx.CommitAsync();
            await Failure(async () => await tx.CommitAsync());
        }
        else
        {
            using var batch = client.CreateBatch();
            await batch.ExecuteAsync();
            await Failure(async () => await batch.ExecuteAsync());
        }
        await Assert.That(hook.Events.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AcknowledgementAdmissionPrecedesWritesAndFailurePreservesSuccessfulMembers(bool aof, bool reject)
    {
        await using var server = Server(failAcknowledgement: true);
        var name = aof ? "WAITAOF" : "WAIT";
        var hook = new Hook { Before = c => { if (reject && c.Command == name) throw new Exception("No durability"); } };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;
        using var batch = client.CreateBatch();
        var pending = batch.Set("key", "value");
        var error = await Failure(async () =>
        {
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero);
        });
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Events.Take(2).SequenceEqual(new[] { "enter SET", "enter " + name })).IsTrue();
        await Assert.That(hook.Completed[0].Outcome).IsEqualTo(reject ? RespireCommandOutcome.Failed : RespireCommandOutcome.Succeeded);
        await Assert.That(hook.Completed[1].Outcome).IsEqualTo(reject ? RespireCommandOutcome.Rejected : RespireCommandOutcome.Failed);
        await Assert.That(ReferenceEquals(hook.Completed[1].Error, error)).IsTrue();
        if (reject) await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        else await Assert.That(pending.Result).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WatchIsAnIndependentSetupScopeAndAbortMatchesPendingOutcome(bool reject)
    {
        await using var server = Server(abort: true);
        var hook = new Hook { Before = c => { if (reject && c.Command == "WATCH") throw new Exception("No watch"); } };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;
        if (reject)
        {
            await Failure(async () => await client.CreateTransactionAsync(["key"]));
            await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        }
        else
        {
            await using var tx = await client.CreateTransactionAsync(["key"]);
            await Assert.That(hook.Completed.Single().Context.Command).IsEqualTo("WATCH");
            var pending = tx.GetString("key");
            await Assert.That(await tx.CommitAsync()).IsFalse();
            await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Aborted);
            await Assert.That(hook.Completed.Count).IsEqualTo(2);
            await Assert.That(hook.Completed[1].Outcome).IsEqualTo(RespireCommandOutcome.Failed);
            await Assert.That(hook.Completed[1].Error is RespireTransactionAbortedException).IsTrue();
            await Assert.That(hook.Events.SequenceEqual(new[] { "enter WATCH", "complete WATCH", "enter GET", "complete GET" })).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InternalRetriesDoNotReadmitOrCompleteIntermediateAttempts(bool transaction)
    {
        await using var server = Server();
        var original = server.ReplyOverride;
        var attempts = 0;
        server.ReplyOverride = (connection, command) =>
        {
            if (command == (transaction ? "EXEC" : "GET key") && Interlocked.Increment(ref attempts) == 1)
            {
                if (transaction) { server.CloseConnections(); return null; }
                return "-TRYAGAIN busy\r\n"u8.ToArray();
            }
            return original!(connection, command);
        };
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero } });
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            var pending = tx.GetString("key");
            await tx.CommitAsync();
            await Assert.That(pending.Result).IsEqualTo("value");
        }
        else
        {
            using var batch = client.CreateBatch();
            var pending = batch.GetString("key");
            await batch.ExecuteAsync();
            await Assert.That(pending.Result).IsEqualTo("value");
        }
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(hook.Events.SequenceEqual(new[] { "enter GET", "complete GET" })).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ImportMemberCompletionRunsOutsideSessionGate(bool transaction, bool reject)
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        hook.Completed.Clear();
        if (reject) hook.Before = _ => throw new InvalidOperationException("No import");
        var outsideGate = false;
        hook.After = () => { using var usage = session.EnterOperation(); outsideGate = true; };
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
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed[0].Outcome).IsEqualTo(reject ? RespireCommandOutcome.Rejected : RespireCommandOutcome.Succeeded);
        hook.Before = null;
        hook.After = null;
        await Assert.That(await session.SetAsync("later", "schema", "value")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationAfterSubmissionCompletesEachMemberOnce(bool transaction)
    {
        await using var server = Server();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != (transaction ? "EXEC" : "GET key")) return false;
            submitted.TrySetResult();
            return true;
        };
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var cts = new CancellationTokenSource();
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            _ = tx.GetString("key");
            var execution = tx.CommitAsync(cts.Token).AsTask();
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Failure(() => execution);
        }
        else
        {
            using var batch = client.CreateBatch();
            _ = batch.GetString("key");
            var execution = batch.TryExecuteAsync(cts.Token).AsTask();
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Assert.That((await execution).FailureCount).IsEqualTo(1);
        }
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed[0].Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DurabilitySuccessAndInitialCancellationCompleteAllEnteredScopes(bool aof, bool cancel)
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var cts = new CancellationTokenSource();
        if (cancel) cts.Cancel();
        var baseline = server.CommandsSeen;
        using var batch = client.CreateBatch();
        var pending = batch.Set("key", "value");
        async Task Execute()
        {
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero, cts.Token);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero, cts.Token);
        }
        if (cancel) await Failure(Execute);
        else await Execute();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == (cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Succeeded))).IsTrue();
        if (cancel)
        {
            await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
            await Assert.That(pending.Error is OperationCanceledException).IsTrue();
        }
        else await Assert.That(pending.Result).IsTrue();
    }

    [Test]
    public async Task RejectingWatchedCommitRetiresWatchWithoutFramingCommands()
    {
        await using var server = Server();
        var hook = new Hook { Before = c => { if (c.Command == "GET") throw new Exception("No GET"); } };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var tx = await client.CreateTransactionAsync(["key"]);
        var pending = tx.GetString("key");
        var baseline = server.CommandsSeen;
        await Failure(async () => await tx.CommitAsync());
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed[1].Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(ReferenceEquals(pending.Error, hook.Completed[1].Error)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WatchCancellationAndSetupFailureAreReported(bool cancel)
    {
        await using var server = Server();
        var original = server.ReplyOverride;
        server.ReplyOverride = (connection, command) => command.StartsWith("WATCH ")
            ? "-ERR watch failed\r\n"u8.ToArray() : original!(connection, command);
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var cts = new CancellationTokenSource();
        if (cancel) cts.Cancel();
        var baseline = server.CommandsSeen;
        var error = await Failure(async () => await client.CreateTransactionAsync(["key"], cts.Token));
        await Assert.That(hook.Completed.Single().Context.Command).IsEqualTo("WATCH");
        await Assert.That(hook.Completed[0].Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Failed);
        await Assert.That(ReferenceEquals(hook.Completed[0].Error, error)).IsTrue();
        if (cancel) await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ApplicationCommandInAdmissionHasItsOwnScope(bool transaction)
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        hook.Before = context =>
        {
            if (context.Command == "STRLEN")
                _ = client.GetStringAsync("nested").AsTask().GetAwaiter().GetResult();
        };
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            _ = tx.Strings.Length("key");
            await tx.CommitAsync();
        }
        else
        {
            using var batch = client.CreateBatch();
            _ = batch.Strings.Length("key");
            await batch.ExecuteAsync();
        }
        await Assert.That(hook.Events.SequenceEqual(new[] { "enter STRLEN", "enter GET", "complete GET", "complete STRLEN" })).IsTrue();
    }

    private static FakeRespServer Server(bool failRead = false, bool failAcknowledgement = false, bool abort = false)
    {
        var transactions = new ConcurrentDictionary<int, List<string>>();
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        string Reply(string command) => command.Split(' ')[0] switch
        {
            "GET" => failRead ? "-ERR read failed\r\n" : "$5\r\nvalue\r\n",
            "STRLEN" => ":5\r\n",
            "WAIT" => failAcknowledgement ? "-ERR durability failed\r\n" : ":1\r\n",
            "WAITAOF" => failAcknowledgement ? "-ERR durability failed\r\n" : "*2\r\n:1\r\n:1\r\n",
            _ => "+OK\r\n",
        };
        server.ReplyOverride = (connection, command) =>
        {
            if (command == "CLIENT ID") return ":1\r\n"u8.ToArray();
            if (command == "MULTI") { transactions[connection] = []; return FakeRespServer.OkReply; }
            if (command == "EXEC" && transactions.TryRemove(connection, out var members))
                return System.Text.Encoding.UTF8.GetBytes(abort ? "*-1\r\n" : $"*{members.Count}\r\n" + string.Concat(members.Select(Reply)));
            if (transactions.TryGetValue(connection, out var queued)) { queued.Add(command); return "+QUEUED\r\n"u8.ToArray(); }
            return System.Text.Encoding.UTF8.GetBytes(Reply(command));
        };
        return server;
    }

    private static RespireOptions Options(FakeRespServer server, Hook hook, Pipeline? pipeline = null) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(3),
        CommandHook = hook, PipelineHook = pipeline,
    };

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure");
    }

    private sealed class Hook : RespireCommandHook
    {
        internal readonly List<string> Events = [];
        internal readonly List<(RespireCommandContext Context, object? State, RespireCommandOutcome Outcome, Exception? Error)> Completed = [];
        internal Action<RespireCommandContext>? Before;
        internal Action? After;
        internal bool ResultsReady;
        public override object? OnExecuting(RespireCommandContext context)
        {
            Events.Add("enter " + context.Command);
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Events.Add("complete " + context.Command);
            Completed.Add((context, state, outcome, error));
            After?.Invoke();
        }
    }

    private sealed class Pipeline(List<string> events) : RespirePipelineHook
    {
        internal Exception? Rejection;
        internal RespirePipelineOutcome Outcome;
        public override object? OnExecuting(RespirePipelineContext context)
        {
            events.Add("pipeline enter");
            if (Rejection is not null) throw Rejection;
            return null;
        }
        public override void OnCompleted(RespirePipelineContext context, object? state, RespirePipelineOutcome outcome, Exception? error)
        {
            Outcome = outcome;
            events.Add("pipeline complete");
        }
    }
}
