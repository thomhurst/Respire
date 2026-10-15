using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class TopologyCommandRetryTests
{
    [Test]
    [Arguments("Sentinel", false)]
    [Arguments("Sentinel", true)]
    [Arguments("Cluster", false)]
    [Arguments("Cluster", true)]
    public async Task TopologyCoalescedProducerOwnsOneTerminalOutcomeAndCanBeReused(string shape, bool exhaust)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence(throwAfterRecording: true);
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await TrackedCacheFixture.ConnectAsync(shape, command =>
            command == "GET key" ? Interlocked.Increment(ref calls) == 1 || exhaust ? TryAgain : Value : null,
            new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero });
        fixture.Server.GateCommand("GET key", release.Task);
        var waiters = Enumerable.Range(0, 10).Select(_ => fixture.ReadAsync(default).AsTask()).ToArray();
        release.TrySetResult();
        if (exhaust)
        {
            foreach (var waiter in waiters)
                await Assert.That(async () => await waiter).ThrowsExactly<RespireServerException>();
        }
        else
        {
            var values = await Task.WhenAll(waiters);
            await Assert.That(values.All(value => value == "value")).IsTrue();
        }
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(1);
        await Assert.That(evidence.Retries.GetValueOrDefault(exhaust ? "exhausted" : "succeeded")).IsEqualTo(1);
        await Assert.That(evidence.InternalErrors).IsEqualTo(1);
        // The producer owns one retry outcome; each public waiter owns its final error.
        await Assert.That(evidence.FinalErrors).IsEqualTo(exhaust ? waiters.Length : 0);
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(2);
        var failedProducer = exhaust;
        exhaust = false;
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(calls).IsEqualTo(failedProducer ? 3 : 2);
    }

    [Test]
    [Arguments(false, "TRYAGAIN")]
    [Arguments(true, "LOADING")]
    public async Task TrackedClusterUnselectedOrDisabledRetriesKeepNativeDispatch(bool enabled, string code)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var reply = Encoding.ASCII.GetBytes($"-{code} rejected\r\n");
        var calls = 0;
        await using var fixture = await TrackedCacheFixture.ConnectAsync("Cluster", command =>
            command == "GET key" ? Interlocked.Increment(ref calls) == 1 ? reply : Value : null,
            enabled ? new() { RetryableErrors = RespireRetryableErrors.TryAgain } : null);
        await Assert.That(async () => await fixture.ReadAsync(default)).ThrowsExactly<RespireServerException>();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(evidence.Retries.Count).IsEqualTo(0);
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(evidence.InternalErrors).IsEqualTo(0);
        await Assert.That(evidence.FinalErrors).IsEqualTo(1);
    }

    [Test]
    public async Task TrackedClusterCancellationDuringBackoffKeepsCallerTokenAndProducerReusable()
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var calls = 0;
        await using var fixture = await TrackedCacheFixture.ConnectAsync("Cluster", command =>
            command == "GET key" ? Interlocked.Increment(ref calls) == 1 ? TryAgain : Value : null,
            new() { InitialBackoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(2), Jitter = 0 },
            coalesce: false);
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.ReadAsync(cancellation.Token).AsTask();
        await evidence.ErrorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Failure(async () => await pending);
        await Assert.That(error).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(evidence.Retries.Count).IsEqualTo(0);
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task TrackedClusterOriginalDeadlineRetiresLateReplyBeforeNextProducer()
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = "$4\r\nlate\r\n"u8.ToArray();
        await using var fixture = await TrackedCacheFixture.ConnectAsync("Cluster", command =>
        {
            if (command != "GET key") return null;
            var number = Interlocked.Increment(ref calls);
            if (number == 1) return TryAgain;
            return number == 2 ? late : Value;
        }, new() { InitialBackoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(2), Jitter = 0 },
            timeout: TimeSpan.FromMilliseconds(3500));
        var elapsed = Stopwatch.StartNew();
        var pending = fixture.ReadAsync(default).AsTask();
        await evidence.ErrorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Server.GateCommand("GET key", release.Task);
        try
        {
            await Assert.That(async () => await pending).ThrowsExactly<RespireTimeoutException>();
            // A new timeout for the second attempt would take at least 5.5 seconds.
            await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(4500));
            // ReplyOverride runs after the gate; count admitted commands while it is held.
            await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
            await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(1);
            await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(0);
        }
        finally { release.TrySetResult(); }
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(3);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task TrackedClusterRedirectsRemainInsideRetryAttemptsAndRebaseCache(string redirect)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence(throwAfterRecording: true);
        var targetReads = 0;
        await using var target = TrackedClusterNode(command => command == "GET key"
            ? Interlocked.Increment(ref targetReads) == 1 ? TryAgain : Value : null);
        var slot = ClusterHash.GetSlot("key");
        var redirectReply = Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n");
        await using var source = TrackedClusterNode(command => command == "GET key" ? redirectReply : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(source) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new() { CoalesceConcurrentMisses = true },
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(targetReads).IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        var sourceReads = redirect == "ASK" ? 2 : 1;
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(sourceReads);
        await Assert.That(source.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(sourceReads);
        await Assert.That(target.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Count(command => command == "ASKING")).IsEqualTo(redirect == "ASK" ? 2 : 0);
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(1);
        await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(1);
        await Assert.That(evidence.InternalErrors).IsEqualTo(sourceReads + 1);
        await Assert.That(evidence.FinalErrors).IsEqualTo(0);
    }

    [Test]
    public async Task TrackedClusterOverlappingProducersKeepCallerSnapshotsIndependent()
    {
        var firstReads = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TrackedClusterNode(command => command switch
        {
            "GET first" => Interlocked.Increment(ref firstReads) == 1 ? TryAgain : "$5\r\nfirst\r\n"u8.ToArray(),
            "GET second" => "$6\r\nsecond\r\n"u8.ToArray(),
            _ => null,
        });
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new() { CoalesceConcurrentMisses = true },
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
        server.GateCommand("GET first", release.Task);
        var firstKey = "first"u8.ToArray();
        var secondKey = "second"u8.ToArray();
        var first = client.GetStringAsync((RespireKey)firstKey).AsTask();
        var second = client.GetStringAsync((RespireKey)secondKey).AsTask();
        try
        {
            "third"u8.CopyTo(firstKey);
            "fourth"u8.CopyTo(secondKey);
            release.TrySetResult();
            await Assert.That(await first).IsEqualTo("first");
            await Assert.That(await second).IsEqualTo("second");
            await Assert.That(firstReads).IsEqualTo(2);
            await Assert.That(server.ReceivedCommands.Count(command => command == "GET second")).IsEqualTo(1);
            await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(3);
            await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task TrackedClusterRepeatedProducersKeepPayloadsAndTerminalMetricsIndependent()
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence(throwAfterRecording: true);
        var calls = 0;
        var exhaust = false;
        await using var server = TrackedClusterNode(command => command == "GET key"
            ? Interlocked.Increment(ref calls) % 2 == 1 || exhaust ? TryAgain : Value : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new() { CoalesceConcurrentMisses = true },
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
        for (var round = 0; round < 32; round++)
        {
            exhaust = round % 2 == 0;
            client.ClientSideCache!.Clear();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.GateCommand("GET key", release.Task);
            var waiters = Enumerable.Range(0, 4).Select(_ => client.GetBytesAsync("key").AsTask()).ToArray();
            release.TrySetResult();
            if (exhaust)
            {
                foreach (var waiter in waiters)
                    await Assert.That(async () => await waiter).ThrowsExactly<RespireServerException>();
            }
            else
            {
                var values = await Task.WhenAll(waiters);
                values[0]![0] = (byte)'X';
                foreach (var value in values.Skip(1))
                    await Assert.That(value).IsEquivalentTo("value"u8.ToArray());
                await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
            }
            await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        }
        await Assert.That(calls).IsEqualTo(64);
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(32);
        await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(16);
        await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(16);
        await Assert.That(evidence.InternalErrors).IsEqualTo(32);
        await Assert.That(evidence.FinalErrors).IsEqualTo(64);
    }

    private static FakeRespServer TrackedClusterNode(Func<string, byte[]?> reply)
    {
        var server = ClusterNode((command, _) => reply(command));
        var original = server.ReplyOverride;
        server.ReplyOverride = (id, command) => command.StartsWith("HELLO", StringComparison.Ordinal)
            ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : command == "CLIENT ID" ? ":1\r\n"u8.ToArray() : original!(id, command);
        return server;
    }

    private sealed class TrackedCacheFixture(FakeRespServer server, RespireClient client,
        FakeRespServer? sentinel) : IAsyncDisposable
    {
        internal FakeRespServer Server => server;

        internal static async Task<TrackedCacheFixture> ConnectAsync(string shape, Func<string, byte[]?> reply,
            RespireRetryPolicy? policy, TimeSpan? timeout = null, bool coalesce = true)
        {
            var server = shape == "Cluster" ? TrackedClusterNode(reply) : Standalone((command, _) =>
                reply(command) ?? (command == "ROLE" ? RoleReply("master") : command.StartsWith("HELLO", StringComparison.Ordinal)
                    ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null));
            FakeRespServer? sentinel = null;
            try
            {
                if (shape == "Sentinel") sentinel = Sentinel(() => server.Port);
                var options = shape == "Sentinel" ? SentinelOptions(sentinel!) : ClusterOptions(server);
                var client = await RespireClient.ConnectAsync(options with
                {
                    RetryPolicy = policy, CommandTimeout = timeout ?? TimeSpan.FromSeconds(5), Connections = 1,
                    Protocol = RespireProtocol.Resp3,
                    ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
                });
                return new(server, client, sentinel);
            }
            catch
            {
                if (sentinel is not null) await sentinel.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        internal ValueTask<string?> ReadAsync(CancellationToken token) => client.GetStringAsync("key", token);

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            if (sentinel is not null) await sentinel.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private sealed class TrackedCacheEvidence : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _internalErrors;
        private long _finalErrors;
        internal readonly ConcurrentDictionary<string, long> Retries = new();
        internal readonly TaskCompletionSource ErrorObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long InternalErrors => Interlocked.Read(ref _internalErrors);
        internal long FinalErrors => Interlocked.Read(ref _finalErrors);

        internal TrackedCacheEvidence(bool throwAfterRecording = false)
        {
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name is "respire.command.retries" or "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (instrument.Name == "respire.command.retries" && tag.Key == "respire.command.retry.outcome")
                        Retries.AddOrUpdate((string)tag.Value!, value, (_, old) => old + value);
                    if (instrument.Name == "redis.client.errors" && tag.Key == "redis.client.errors.internal")
                    {
                        if (Equals(tag.Value, true)) Interlocked.Add(ref _internalErrors, value);
                        else Interlocked.Add(ref _finalErrors, value);
                        ErrorObserved.TrySetResult();
                    }
                }
                if (throwAfterRecording) throw new InvalidOperationException("Injected listener failure.");
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
