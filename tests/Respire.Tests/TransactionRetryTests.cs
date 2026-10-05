using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TransactionRetryTests
{
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
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConflictsAndStartedRetriesEmitCounters(bool throwingListener)
    {
        var measurements = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name.StartsWith("respire.transaction.watch.", StringComparison.Ordinal))
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
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
            .Throws<ArgumentOutOfRangeException>();
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
