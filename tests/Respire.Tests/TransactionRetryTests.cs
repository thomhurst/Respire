using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TransactionRetryTests
{
    // The transaction counters are process-wide and concurrent tests also run transactions.
    // Measurement callbacks run on the recording thread inside the caller's async flow, so a
    // fresh ambient trace marks the measurements a test caused.
    private static Activity StartTestTrace()
        => new Activity("respire-transaction-retry-test")
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom())
            .Start();

    private static bool IsInTrace(Activity trace) => Activity.Current?.TraceId == trace.TraceId;

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReadOnlyDecisionValidatesWatchBeforeReturning(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.SetAsync("balance", "1");
        var attempts = 0;
        var result = await client.RunTransactionAsync(["balance"], async (_, token) =>
        {
            var balance = await client.GetStringAsync("balance", token);
            if (++attempts == 1) await client.SetAsync("balance", "2", cancellationToken: token);
            return balance;
        });

        await Assert.That(result).IsEqualTo("2");
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentWritersConvergeAfterConflicts(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        const int writers = 8;
        var arrived = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var attempts = new int[writers];
        var tasks = Enumerable.Range(0, writers).Select(async index =>
            await client.RunTransactionAsync(["balance"], async (transaction, token) =>
            {
                attempts[index]++;
                var balance = long.Parse(await client.GetStringAsync("balance", token) ?? "0", CultureInfo.InvariantCulture);
                if (attempts[index] == 1)
                {
                    if (Interlocked.Increment(ref arrived) == writers) ready.TrySetResult();
                    await ready.Task.WaitAsync(token);
                }
                transaction.Set("balance", balance + 1);
                return balance + 1;
            }, new RespireTransactionRetryOptions { MaxAttempts = writers * 2 }, cancellation.Token).AsTask()).ToArray();

        var results = await Task.WhenAll(tasks);
        await Assert.That(await client.GetStringAsync("balance")).IsEqualTo(writers.ToString(CultureInfo.InvariantCulture));
        await Assert.That(results.Order().ToArray()).IsEquivalentTo(Enumerable.Range(1, writers).Select(value => (long)value).ToArray());
        await Assert.That(attempts.Sum()).IsGreaterThan(writers);
    }

    [Test]
    public async Task AttemptLimitReportsExactCountAndDiscardsPendingWrites()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var attempts = 0;
        var pending = new List<RespirePending<long>>();
        RespireTransactionConflictException? failure = null;
        try
        {
            await client.RunTransactionAsync(["watched"], async (transaction, token) =>
            {
                attempts++;
                await client.IncrementAsync("watched", cancellationToken: token);
                pending.Add(transaction.Increment("result"));
            }, new RespireTransactionRetryOptions { MaxAttempts = 3 });
        }
        catch (RespireTransactionConflictException error) { failure = error; }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Attempts).IsEqualTo(3);
        await Assert.That(attempts).IsEqualTo(3);
        await Assert.That(pending.All(value => value.Status == RespirePendingStatus.Aborted)).IsTrue();
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    public async Task CallbackExceptionPropagatesWithoutCommitOrRetry()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var attempts = 0;
        var expected = new InvalidOperationException("callback failed");
        Exception? observed = null;
        try
        {
            await client.RunTransactionAsync(["watched"], (transaction, _) =>
            {
                attempts++;
                transaction.Set("result", "never committed");
                throw expected;
            });
        }
        catch (Exception error) { observed = error; }

        await Assert.That(ReferenceEquals(observed, expected)).IsTrue();
        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LostCommitReplyIsNeverReplayed(bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(afterExecution));
        var attempts = 0;
        await Assert.That(async () => await client.RunTransactionAsync(["result"], (transaction, _) =>
        {
            attempts++;
            transaction.Increment("result");
            return ValueTask.CompletedTask;
        })).Throws<RespireConnectionException>();

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await using var observer = await RespireClient.ConnectAsync(server.CreateOptions());
        await Assert.That(await observer.GetStringAsync("result")).IsEqualTo(afterExecution ? "1" : null);
    }

    [Test]
    public async Task CancellationDuringBackoffDoesNotStartAnotherAttempt()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var delays = new List<int>();
        await Assert.That(async () => await client.RunTransactionAsync(["watched"], async (transaction, token) =>
        {
            attempts++;
            await client.IncrementAsync("watched", cancellationToken: token);
            transaction.Increment("result");
        }, new RespireTransactionRetryOptions
        {
            Backoff = attempt => { delays.Add(attempt); cancellation.Cancel(); return TimeSpan.FromHours(1); },
        }, cancellation.Token)).Throws<OperationCanceledException>();

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(delays.ToArray()).IsEquivalentTo(new[] { 1 });
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    public async Task KeysAreSnapshottedBeforeFirstCallbackAndRemainStableAcrossRetries()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        byte[] binaryKey = "original"u8.ToArray();
        RespireKey[] keys = [binaryKey];
        var attempts = 0;
        await client.RunTransactionAsync(keys, async (transaction, token) =>
        {
            if (++attempts == 1)
            {
                Array.Fill(binaryKey, (byte)'x');
                keys[0] = "other";
                await client.IncrementAsync("original", cancellationToken: token);
            }
            transaction.Increment("result");
        });

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(await client.GetStringAsync("result")).IsEqualTo("1");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConflictsAndStartedRetriesEmitCounters(bool throwingListener)
    {
        var measurements = new ConcurrentDictionary<string, long>();
        using var trace = StartTestTrace();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name.StartsWith("respire.transaction.watch.", StringComparison.Ordinal))
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (!IsInTrace(trace)) return;
            measurements.AddOrUpdate(instrument.Name, value, (_, previous) => previous + value);
            if (throwingListener) throw new InvalidOperationException("listener failed");
        });
        listener.Start();
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var attempts = 0;
        await client.RunTransactionAsync(["watched"], async (transaction, token) =>
        {
            if (++attempts == 1) await client.IncrementAsync("watched", cancellationToken: token);
            transaction.Increment("result");
        });

        await Assert.That(measurements["respire.transaction.watch.conflicts"]).IsEqualTo(1);
        await Assert.That(measurements["respire.transaction.watch.retries"]).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidAttemptLimitFailsBeforeCallbackOrConnection()
    {
        await using var client = RespireClient.Create("127.0.0.1:1");
        await Assert.That(async () => await client.RunTransactionAsync([], (_, _) => ValueTask.CompletedTask,
            new RespireTransactionRetryOptions { MaxAttempts = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task NonGenericValidationThrowsSynchronously()
    {
        await using var client = RespireClient.Create("127.0.0.1:1");
        Assert.Throws<ArgumentNullException>(() => { _ = client.RunTransactionAsync([], null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = client.RunTransactionAsync(null!, (_, _) => ValueTask.CompletedTask); });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = client.RunTransactionAsync([], (_, _) => ValueTask.CompletedTask,
                new RespireTransactionRetryOptions { MaxAttempts = 0 });
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = client.RunTransactionAsync([], (_, _) => ValueTask.CompletedTask, cancellationToken: cancellation.Token);
        });
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedRetryAcquisitionDoesNotEmitRetryCounter(bool cancel)
    {
        long retries = 0;
        using var trace = StartTestTrace();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.transaction.watch.retries")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (IsInTrace(trace)) Interlocked.Add(ref retries, value);
        });
        listener.Start();
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var cancellation = new CancellationTokenSource();
        IDisposable? fault = null;
        var callbacks = 0;
        Exception? failure = null;
        try
        {
            await client.RunTransactionAsync(["watched"], async (transaction, token) =>
            {
                callbacks++;
                await client.IncrementAsync("watched", cancellationToken: token);
                _ = transaction.Increment("result");
            }, new RespireTransactionRetryOptions
            {
                Backoff = _ =>
                {
                    if (cancel) cancellation.Cancel();
                    else fault = server.InjectFault("WATCH", RespireFakeFault.Loading());
                    return TimeSpan.Zero;
                },
            }, cancellation.Token);
        }
        catch (Exception error) { failure = error; }
        finally { fault?.Dispose(); }

        await Assert.That(cancel ? failure is OperationCanceledException : failure is RespireServerException).IsTrue();
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(retries).IsEqualTo(0);
    }

    [Test]
    public async Task ExponentialBackoffBoundsDelayAndValidatesInputs()
    {
        var backoff = RespireTransactionRetryOptions.ExponentialBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(25));
        foreach (var (attempt, ceiling) in new[] { (1, 10), (2, 20), (3, 25), (int.MaxValue, 25) })
        {
            for (var sample = 0; sample < 32; sample++)
            {
                var delay = backoff(attempt);
                await Assert.That(delay >= TimeSpan.Zero && delay <= TimeSpan.FromMilliseconds(ceiling)).IsTrue();
            }
        }
        await Assert.That(RespireTransactionRetryOptions.ExponentialBackoff(TimeSpan.Zero, TimeSpan.Zero)(int.MaxValue)).IsEqualTo(TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => backoff(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespireTransactionRetryOptions.ExponentialBackoff(TimeSpan.FromTicks(-1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespireTransactionRetryOptions.ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespireTransactionRetryOptions.ExponentialBackoff(TimeSpan.Zero, TimeSpan.MaxValue));
    }

    [Test]
    public async Task ClusterCrossSlotWatchFailsBeforeCallbackOrConnection()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], UseCluster = true,
        });
        var callbacks = 0;
        await Assert.That(async () => await client.RunTransactionAsync(["slot-a", "slot-b"], (_, _) =>
        {
            callbacks++;
            return ValueTask.CompletedTask;
        })).Throws<InvalidOperationException>();
        await Assert.That(callbacks).IsEqualTo(0);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task GenericPendingResultIsCompletedBeforeReturning()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var pending = await client.RunTransactionAsync(["result"], (transaction, _) =>
            ValueTask.FromResult(transaction.Increment("result")));
        await Assert.That(pending.Result).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidBackoffStopsAfterFirstConflict()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var attempts = 0;
        await Assert.That(async () => await client.RunTransactionAsync(["watched"], async (transaction, token) =>
        {
            attempts++;
            await client.IncrementAsync("watched", cancellationToken: token);
            transaction.Increment("result");
        }, new RespireTransactionRetryOptions { Backoff = _ => TimeSpan.FromMilliseconds(-1) }))
            .Throws<InvalidOperationException>();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationAfterCallbackPreventsCommit()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.That(async () => await client.RunTransactionAsync(["result"], (transaction, _) =>
        {
            attempts++;
            transaction.Increment("result");
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }
}
