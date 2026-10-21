using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Networking;
using Respire.Testing;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TransactionTransientRetryTests
{
    private static RespireOptions Options(RespireFakeServer server,
        RespireCommandRetryCategory category = RespireCommandRetryCategory.ReadOnly, int attempts = 3)
        => server.CreateOptions() with
        {
            RetryPolicy = new() { MaxAttempts = attempts, MaxCommandRetryCategory = category, InitialBackoff = TimeSpan.Zero },
        };

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WatchFailureReacquiresWatchBeforeRunningCallback(bool disconnect)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        using var fault = server.InjectFault("WATCH", disconnect ? RespireFakeFault.Disconnect() : RespireFakeFault.Loading());
        var callbacks = 0;
        await client.RunTransactionAsync(["result"], (transaction, _) =>
        {
            callbacks++;
            transaction.Increment("result");
            return ValueTask.CompletedTask;
        });
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("result")).IsEqualTo("1");
    }

    [Test]
    public async Task DefinitelyUnwrittenCommitReplaysAccumulatingMemberUnderReadOnlyPolicy()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        var callbacks = 0;
        var transactions = new List<RespireWatchedTransaction>();
        var pending = await client.RunTransactionAsync(["result"], async (transaction, _) =>
        {
            transactions.Add(transaction);
            var value = transaction.Increment("result");
            if (++callbacks == 1) await transaction.InspectForTests().WatchConnection!.DisposeAsync();
            return value;
        });
        await Assert.That(callbacks).IsEqualTo(2);
        await Assert.That(pending.Result).IsEqualTo(1);
        await Assert.That(() => transactions[0].Increment("result")).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task LostExecReplyRequiresPermissionAndRebuildsReads(bool afterExecution, bool permitted)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server,
            permitted ? RespireCommandRetryCategory.WriteAccumulating : RespireCommandRetryCategory.ReadOnly));
#pragma warning restore RESP010
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(afterExecution));
        var callbacks = 0;
        var inputs = new List<string?>();
        var pendings = new List<RespirePending<long>>();
        var operation = client.RunTransactionWithReadsAsync(["result"], async (reads, transaction, token) =>
        {
            callbacks++;
            inputs.Add(await reads.GetStringAsync("result", token));
            pendings.Add(transaction.Increment("result"));
            return callbacks;
        }).AsTask();
        if (permitted)
        {
            await Assert.That(await operation).IsEqualTo(2);
            await Assert.That(callbacks).IsEqualTo(2);
            await Assert.That(inputs[1]).IsEqualTo(afterExecution ? "1" : null);
            await Assert.That(pendings[1].Result).IsEqualTo(afterExecution ? 2 : 1);
        }
        else
        {
            await Assert.That(async () => await operation).Throws<RespireConnectionException>();
            await Assert.That(callbacks).IsEqualTo(1);
        }
        // A lost reply faults the pending; it never becomes a WATCH abort.
        await Assert.That(pendings[0].Error is RespireConnectionException).IsTrue();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var observer = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        await Assert.That(await observer.GetStringAsync("result")).IsEqualTo(
            permitted ? afterExecution ? "2" : "1" : afterExecution ? "1" : null);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryQueuedMemberMustPermitReplay(bool unknown)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server,
            unknown ? RespireCommandRetryCategory.ServerAdmin : RespireCommandRetryCategory.WriteLastWins));
#pragma warning restore RESP010
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(true));
        var callbacks = 0;
        await Assert.That(async () => await client.RunTransactionAsync(["result"], (transaction, _) =>
        {
            callbacks++;
            transaction.Set("result", "1");
            if (unknown) transaction.Execute("GET", "result");
            else transaction.Increment("result");
            return ValueTask.CompletedTask;
        })).Throws<RespireConnectionException>();
        await Assert.That(callbacks).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConflictsAndTransientFailuresUseIndependentBudgets(bool exhaustConflicts)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server, attempts: 2));
#pragma warning restore RESP010
        var callbacks = 0;
        Exception? failure = null;
        try
        {
            await client.RunTransactionAsync(["watched"], async (transaction, token) =>
            {
                var attempt = ++callbacks;
                _ = transaction.Set("result", "done");
                if (attempt is 1 or 3 || exhaustConflicts && attempt == 4)
                    await client.IncrementAsync("watched", cancellationToken: token);
                if (attempt == 2) throw new RespireConnectionException("callback read connection lost");
            }, new() { MaxAttempts = 3 });
        }
        catch (Exception error) { failure = error; }
        await Assert.That(callbacks).IsEqualTo(4);
        if (exhaustConflicts)
        {
            await Assert.That(failure is RespireTransactionConflictException { Attempts: 3 }).IsTrue();
            await Assert.That(await client.ExistsAsync("result")).IsFalse();
        }
        else
        {
            await Assert.That(failure).IsNull();
            await Assert.That(await client.GetStringAsync("result")).IsEqualTo("done");
        }
    }

    [Test]
    public async Task ConflictDoesNotResetTransientBudget()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server, attempts: 2));
#pragma warning restore RESP010
        var callbacks = 0;
        await Assert.That(async () => await client.RunTransactionAsync(["watched"], async (transaction, token) =>
        {
            _ = transaction.Set("result", "done");
            if (++callbacks == 2) await client.IncrementAsync("watched", cancellationToken: token);
            else throw new RespireConnectionException("callback read connection lost");
        }, new() { MaxAttempts = 2 })).Throws<RespireConnectionException>();
        await Assert.That(callbacks).IsEqualTo(3);
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    public async Task WatchAcquisitionExhaustionDoesNotRunCallback()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server, attempts: 2));
#pragma warning restore RESP010
        using var fault = server.InjectFault("WATCH", RespireFakeFault.Loading(), occurrences: null);
        var callbacks = 0;
        await Assert.That(async () => await client.RunTransactionAsync(["watched"], (_, _) =>
        {
            callbacks++;
            return ValueTask.CompletedTask;
        })).Throws<RespireServerException>();
        await Assert.That(fault.MatchedCount).IsEqualTo(2);
        await Assert.That(callbacks).IsEqualTo(0);
    }

    [Test]
    public async Task DirectWatchedCommitDoesNotReplayEvenWithAccumulatingPermission()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteAccumulating));
#pragma warning restore RESP010
        await using var transaction = await client.CreateTransactionAsync(["result"]);
        _ = transaction.Increment("result");
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(true), occurrences: null);
        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("result")).IsEqualTo("1");
    }

    [Test]
    public async Task OneDeadlineCoversCallbacksAndConflictBackoff()
    {
        await using var server = new RespireFakeServer();
        // Network progress has a generous watchdog; only the logical timeout is
        // advanced by the test, after the intended callback/backoff has been reached.
        var budget = TimeSpan.FromHours(1);
        var clock = new ClusterRecoveryTestClock();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with { CommandTimeout = budget });
#pragma warning restore RESP010
        var callbacks = 0;
        var backoffs = new List<int>();
        var deadlines = new List<long>();
        var tokens = new List<CancellationToken>();
        var timeoutCreations = 0;
        var options = new RespireTransactionRetryOptions
        {
            TimeoutFactory = (_, timeout) =>
            {
                timeoutCreations++;
                return new CancellationTokenSource(timeout, clock);
            },
            Backoff = attempt =>
            {
                backoffs.Add(attempt);
                clock.Advance(budget / 4);
                return TimeSpan.FromMilliseconds(1);
            },
        };
        await Assert.That(async () => await client.RunTransactionAsync(["watched"], async (transaction, token) =>
        {
            callbacks++;
            tokens.Add(token);
            deadlines.Add(transaction.HelperRetryContext!.Deadline.Ticks);
            if (callbacks == 1)
            {
                clock.Advance(budget / 2);
                await client.IncrementAsync("watched", cancellationToken: token);
            }
            else
            {
                clock.Advance(budget / 4);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            _ = transaction.Set("result", "done");
        }, options).AsTask().WaitAsync(TimeSpan.FromSeconds(30))).Throws<RespireTimeoutException>();
        await Assert.That(callbacks).IsEqualTo(2);
        await Assert.That(timeoutCreations).IsEqualTo(1);
        await Assert.That(backoffs.SequenceEqual([1])).IsTrue();
        await Assert.That(deadlines[0]).IsNotEqualTo(0);
        await Assert.That(deadlines[0]).IsEqualTo(deadlines[1]);
        await Assert.That(tokens[0]).IsEqualTo(tokens[1]);
        await Assert.That(tokens[1].IsCancellationRequested).IsTrue();
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    public async Task CancellationAfterTransientFailureKeepsCallerTokenAndDisposesAttempt()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(1), Jitter = 0 },
        });
#pragma warning restore RESP010
        using var caller = new CancellationTokenSource();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RespireWatchedTransaction? attempt = null;
        var callbacks = 0;
        var operation = client.RunTransactionAsync(["watched"], (transaction, _) =>
        {
            callbacks++;
            attempt = transaction;
            transaction.Set("result", "done");
            returned.SetResult();
            throw new RespireConnectionException("callback read failed");
        }, cancellationToken: caller.Token).AsTask();
        await returned.Task;
        caller.Cancel();
        var error = await Assert.That(async () => await operation).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(() => attempt!.Increment("result")).Throws<InvalidOperationException>();
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransientRetryTelemetryReportsOutcomeWithoutWatchConflict(bool exhaust)
    {
        using var trace = new Activity("transaction-transient-test")
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom()).Start();
        var events = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && (instrument.Name == "respire.command.retries"
                || instrument.Name.StartsWith("respire.transaction.watch.", StringComparison.Ordinal)))
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (Activity.Current?.TraceId != trace.TraceId) return;
            var name = instrument.Name;
            foreach (var tag in tags)
                if (tag.Key == "respire.command.retry.outcome") name = (string)tag.Value!;
            events.AddOrUpdate(name, value, (_, old) => old + value);
        });
        listener.Start();
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteAccumulating, 2));
#pragma warning restore RESP010
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(true), occurrences: exhaust ? null : 1);
        var operation = client.RunTransactionAsync(["result"], (transaction, _) =>
        {
            transaction.Increment("result");
            return ValueTask.CompletedTask;
        }).AsTask();
        if (exhaust) await Assert.That(async () => await operation).Throws<RespireConnectionException>();
        else await operation;
        await Assert.That(events["attempted"]).IsEqualTo(1);
        await Assert.That(events[exhaust ? "exhausted" : "succeeded"]).IsEqualTo(1);
        await Assert.That(events.Keys.Any(name => name.StartsWith("respire.transaction.watch.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task UnknownCategoryNeverRetriesEvenWhenDefinitelyUnwritten()
    {
        var context = new WatchedTransactionRetryContext(CommandDeadline.None);
        context.RecordCategory(RespireCommandRetryCategory.Never);
        var policy = new CommandRetryPolicy(new() { MaxCommandRetryCategory = RespireCommandRetryCategory.ServerAdmin });
        await Assert.That(context.TryGetDelay(policy, new RespireConnectionException("unwritten"), 1,
            TimeSpan.MaxValue, default, out _)).IsFalse();
    }

    [Test]
    public async Task TransientAndConflictAttemptsKeepSameDeadlineAndDisposePreviousWatch()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        var deadlines = new List<long>();
        var tokens = new List<CancellationToken>();
        RespireConnection? previous = null;
        var callbacks = 0;
        await client.RunTransactionAsync(["watched"], async (transaction, token) =>
        {
            if (callbacks == 1) await Assert.That(previous!.IsConnected).IsFalse();
            previous = transaction.InspectForTests().WatchConnection;
            deadlines.Add(transaction.HelperRetryContext!.Deadline.Ticks);
            tokens.Add(token);
            _ = transaction.Set("result", "done");
            if (++callbacks == 1) throw new RespireConnectionException("before EXEC");
            if (callbacks == 2) await client.IncrementAsync("watched", cancellationToken: token);
        });
        await Assert.That(callbacks).IsEqualTo(3);
        await Assert.That(deadlines.Distinct().Count()).IsEqualTo(1);
        await Assert.That(tokens.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task DeadlineInterruptsConflictBackoffBeforeAnotherWatch()
    {
        await using var server = new RespireFakeServer();
        var budget = TimeSpan.FromHours(1);
        var clock = new ClusterRecoveryTestClock();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with { CommandTimeout = budget });
#pragma warning restore RESP010
        var callbacks = 0;
        var backoffs = new List<int>();
        CancellationToken callbackToken = default;
        RespireWatchedTransaction? attempt = null;
        await Assert.That(async () => await client.RunTransactionAsync(["watched"], async (transaction, token) =>
        {
            callbacks++;
            callbackToken = token;
            attempt = transaction;
            _ = transaction.Set("result", "done");
            await client.IncrementAsync("watched", cancellationToken: token);
        }, new()
        {
            TimeoutFactory = (_, timeout) => new CancellationTokenSource(timeout, clock),
            Backoff = conflict =>
            {
                backoffs.Add(conflict);
                clock.Advance(budget);
                return TimeSpan.FromHours(1);
            },
        }).AsTask().WaitAsync(TimeSpan.FromSeconds(30))).Throws<RespireTimeoutException>();
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(backoffs.SequenceEqual([1])).IsTrue();
        await Assert.That(callbackToken.IsCancellationRequested).IsTrue();
        await Assert.That(() => attempt!.Increment("result")).Throws<InvalidOperationException>();
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    public async Task RawQueuedDeclarationParticipatesInWholeCallbackReplay()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns and disposes its client to isolate configuration and connection state.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteLastWins));
#pragma warning restore RESP010
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(true));
        var callbacks = 0;
        await client.RunTransactionAsync(["result"], (transaction, _) =>
        {
            callbacks++;
            transaction.Execute(RespireCommand.Create("SET").WithRetryCategory(RespireCommandRetryCategory.WriteLastWins), "result", "done");
            return ValueTask.CompletedTask;
        });
        await Assert.That(callbacks).IsEqualTo(2);
        await Assert.That(await client.GetStringAsync("result")).IsEqualTo("done");
    }
}
