using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamRecoveryChaosIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task InterruptedBatchRecoversEveryMessageAndCompletesPoisonPolicy(bool useFake, int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = useFake ? server.CreateOptions() : RespireOptions.Parse(redis.ConnectionString);
        await using var root = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix("{chaos}tenant:");
        var ids = Enumerable.Range(1, 12).Select(index => $"{index}-0").ToArray();
        var poisonIds = ids.Where((_, index) => index % 3 == 0).ToArray();
        byte[] payload = [0xff, 0, 13, 10];
        for (var index = 0; index < ids.Length; index++)
            await client.Streams.AddAsync("events", new StreamAddOptions { Id = ids[index] },
                ("kind", index % 3 == 0 ? "poison" : "normal"), ("payload", payload));
        await client.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        await client.Streams.CreateGroupAsync("events", "independent", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("events", "independent", "observer", new() { Count = ids.Length });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var interrupted = new ChaosState { Interrupt = true };
        await using (var stopped = CreateWorker(client, interrupted))
        {
            var worker = stopped.GetServices<IHostedService>().Single();
            await worker.StartAsync(deadline.Token);
            try
            {
                await interrupted.BothEntered.Task.WaitAsync(deadline.Token);
                await worker.StopAsync(new CancellationToken(canceled: true));
                await interrupted.BothExited.Task.WaitAsync(deadline.Token);
                // Two active handlers and two undispatched entries were delivered before shutdown.
                await Assert.That((await client.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(4);
                await Assert.That(interrupted.Attempts.Values.Sum()).IsEqualTo(2);
                await Assert.That(await client.Streams.CountAsync("dead")).IsEqualTo(0);
            }
            finally { await worker.StopAsync(new CancellationToken(canceled: true)); }
        }

        var recovered = new ChaosState();
        await using var provider = CreateWorker(client, recovered);
        var recovery = provider.GetServices<IHostedService>().Single();
        await recovery.StartAsync(deadline.Token);
        try
        {
            await recovered.BothEntered.Task.WaitAsync(deadline.Token);
            // Both consumers must overlap before any recovery handler can complete.
            await Assert.That(Volatile.Read(ref recovered.Active)).IsEqualTo(2);
            await Assert.That(recovered.MaximumActive).IsEqualTo(2);
            recovered.ReleaseHandlers.TrySetResult();

            while (await client.Streams.CountAsync("dead", deadline.Token) != poisonIds.Length
                || (await client.Streams.PendingSummaryAsync("events", "workers", deadline.Token)).Count != 0
                || recovered.Attempts.Count != ids.Length)
                await Task.Delay(10, deadline.Token);
            await recovery.StopAsync(deadline.Token);

            var deadLetters = await client.Streams.ReadAsync("dead", cancellationToken: deadline.Token);
            await Assert.That(deadLetters.Select(entry => entry.GetString("_respire.source_id")!)).IsEquivalentTo(poisonIds);
            foreach (var entry in deadLetters)
            {
                await Assert.That(entry.GetString("_respire.attempt")).IsEqualTo("3");
                await Assert.That(entry.GetString("kind")).IsEqualTo("poison");
                await Assert.That(entry["payload"].SequenceEqual(payload)).IsTrue();
            }
            foreach (var id in ids)
            {
                await Assert.That(recovered.Attempts[id]).IsGreaterThanOrEqualTo(1);
                await Assert.That(recovered.Attempts[id] + interrupted.Attempts.GetValueOrDefault(id)).IsLessThanOrEqualTo(3);
            }
            await Assert.That(recovered.Payloads.Select(delivery => delivery.Id).Distinct()).IsEquivalentTo(ids);
            foreach (var delivery in interrupted.Payloads.Concat(recovered.Payloads))
                await Assert.That(delivery.Payload.SequenceEqual(payload)).IsTrue();
            await Assert.That(recovered.MaximumActive).IsLessThanOrEqualTo(2);
            await Assert.That(recovered.Completed.Keys).IsEquivalentTo(ids.Except(poisonIds));
            await Assert.That((await client.Streams.PendingSummaryAsync("events", "independent")).Count).IsEqualTo(ids.Length);
            await Assert.That(await client.Streams.CountAsync("events")).IsEqualTo(ids.Length);
            await Assert.That(await root.Streams.CountAsync("events")).IsEqualTo(0);
            await Assert.That(await root.Streams.CountAsync("{chaos}tenant:{chaos}tenant:dead")).IsEqualTo(0);
        }
        finally
        {
            recovered.ReleaseHandlers.TrySetResult();
            await recovery.StopAsync(new CancellationToken(canceled: true));
        }
    }

    private static ServiceProvider CreateWorker(IRespireClient client, ChaosState state)
    {
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
        services.AddRespireStreamWorker<ChaosHandler>("events", "workers", new()
        {
            ConsumerCount = 2, BatchSize = 2, DeadLetterStream = "dead", DeliveryLimit = 3,
            MinimumIdleTime = TimeSpan.FromMilliseconds(100), RecoveryPollInterval = TimeSpan.FromMilliseconds(20),
        });
        return services.BuildServiceProvider();
    }

    public sealed class ChaosState
    {
        public bool Interrupt { get; init; }
        public ConcurrentDictionary<string, int> Attempts { get; } = new();
        public ConcurrentDictionary<string, bool> Completed { get; } = new();
        public ConcurrentQueue<(string Id, byte[] Payload)> Payloads { get; } = new();
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHandlers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active;
        public int MaximumActive;
        public int Exited;
    }

    public sealed class ChaosHandler(ChaosState state) : IRespireStreamHandler<RespireStreamEntry>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry entry, CancellationToken token)
        {
            var invocation = state.Attempts.AddOrUpdate(entry.Id.Value, 1, static (_, count) => count + 1);
            state.Payloads.Enqueue((entry.Id.Value, entry["payload"]?.ToArray() ?? []));
            var active = Interlocked.Increment(ref state.Active);
            lock (state) state.MaximumActive = Math.Max(state.MaximumActive, active);
            try
            {
                if (active == 2) state.BothEntered.TrySetResult();
                if (state.Interrupt)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    // Even an Ack returned after shutdown cancellation must remain pending.
                    return RespireStreamWorkerResult.Ack;
                }
                await state.ReleaseHandlers.Task.WaitAsync(token);
                if (entry.GetString("kind") == "poison")
                {
                    if (invocation % 2 == 0) throw new InvalidOperationException("private poison payload");
                    return RespireStreamWorkerResult.Nack;
                }
                state.Completed.TryAdd(entry.Id.Value, true);
                return RespireStreamWorkerResult.Ack;
            }
            finally
            {
                Interlocked.Decrement(ref state.Active);
                if (state.Interrupt && Interlocked.Increment(ref state.Exited) == 2) state.BothExited.TrySetResult();
            }
        }
    }
}
