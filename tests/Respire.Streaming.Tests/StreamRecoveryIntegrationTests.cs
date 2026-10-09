using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Internal;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamRecoveryIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BoundedCursorDeletedPendingAndAttemptFencingMatchRedis(int protocol)
    {
        await using var real = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        await using var server = new RespireFakeServer();
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        foreach (var root in new[] { real, fake })
        {
            var client = root.WithKeyPrefix("tenant:");
            for (var i = 1; i <= 23; i++)
                await client.Streams.AddAsync("s", new StreamAddOptions { Id = $"{i}-0" }, ("payload", i));
            await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("s", "g", "abandoned", new() { Count = 23 });
            using (var fresh = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "new", 60000, "0-0", 1]))
            {
                await Assert.That(fresh[0].AsString()).IsEqualTo("11-0");
                await Assert.That(fresh[1].Count).IsEqualTo(0);
            }
            // Removed bodies still occupy PEL slots until the scan clears them.
            await client.Streams.RemoveAsync("s", ["1-0", "2-0"]);
            using (var deleted = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "new", 60000, "0-0", 1]))
            {
                await Assert.That(deleted[0].AsString()).IsEqualTo("2-0");
                await Assert.That(deleted[1].Count).IsEqualTo(0);
            }
            using (var deleted = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "new", 60000, "2-0", 1]))
            {
                await Assert.That(deleted[0].AsString()).IsEqualTo("3-0");
                await Assert.That(deleted[1].Count).IsEqualTo(0);
            }
            using (var second = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "new", 60000, "11-0", 1]))
                await Assert.That(second[0].AsString()).IsEqualTo("21-0");
            using var claimed = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "new", 0, "21-0", 1]);
            await Assert.That(claimed[0].AsString()).IsEqualTo("22-0");
            await Assert.That(claimed[1][0][0].AsString()).IsEqualTo("21-0");
            await Assert.That(claimed[1][0][1][1].AsString()).IsEqualTo("21");
            await Assert.That(claimed[1][0][2].AsString()).IsEqualTo("2");
            using var sameName = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["s"],
                ["g", "new", 0, "21-0", 1]);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Ack, ["s"],
                ["g", "new", "21-0", 2])).IsEqualTo(0);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Ack, ["s"],
                ["g", "abandoned", "21-0", 3])).IsEqualTo(0);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Ack, ["s"],
                ["g", "new", "21-0", 3])).IsEqualTo(1);
            await Assert.That((await client.Streams.PendingSummaryAsync("s", "g")).Count).IsEqualTo(20);
            await Assert.That(await root.Streams.CountAsync("s")).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task StoppedConsumerIsRecoveredByAnotherHostAtLeastOnce(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant:");
        await view.Streams.AddAsync("events", ("payload", "recover"));
        var entered = Channel.CreateUnbounded<RespireStreamEntry>();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IRespireClient>(view).AddSingleton(entered);
        builder.Services.AddRespireStreamWorker<StoppedHandler>("events", "workers");
        using var stopped = builder.Build();
        await stopped.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await entered.Reader.ReadAsync(deadline.Token);
        await stopped.StopAsync(new CancellationToken(canceled: true));
        await Assert.That((await view.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);

        var recovered = Channel.CreateUnbounded<RespireStreamEntry>();
        var next = Host.CreateApplicationBuilder();
        next.Services.AddSingleton<IRespireClient>(view).AddSingleton(recovered);
        next.Services.AddRespireStreamWorker<RecoveredHandler>("events", "workers", new()
        {
            MinimumIdleTime = TimeSpan.FromMilliseconds(50), RecoveryPollInterval = TimeSpan.FromMilliseconds(20),
        });
        using var recovery = next.Build();
        await recovery.StartAsync();
        try
        {
            var entry = await recovered.Reader.ReadAsync(deadline.Token);
            await Assert.That(entry.GetString("payload")).IsEqualTo("recover");
            while ((await view.Streams.PendingSummaryAsync("events", "workers")).Count != 0)
                await Task.Delay(10, deadline.Token);
        }
        finally { await recovery.StopAsync(deadline.Token); }
    }

    public sealed class StoppedHandler(Channel<RespireStreamEntry> entered) : IRespireStreamHandler<RespireStreamEntry>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry entry, CancellationToken token)
        {
            entered.Writer.TryWrite(entry);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RespireStreamWorkerResult.Ack;
        }
    }

    [Test]
    [Arguments(2, false, false)]
    [Arguments(3, false, false)]
    [Arguments(2, true, false)]
    [Arguments(3, true, false)]
    [Arguments(2, false, true)]
    [Arguments(3, false, true)]
    [Arguments(2, true, true)]
    [Arguments(3, true, true)]
    public async Task RealWorkerRejectsStaleCompletionAfterOwnerOrAttemptChanges(int protocol, bool sameConsumer, bool replay)
    {
        await using var root = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix("tenant:");
        await client.Streams.AddAsync("stale", new StreamAddOptions { Id = "1-0" }, ("payload", "body"));
        await client.Streams.CreateGroupAsync("stale", "workers", RespireStreamId.Beginning);
        if (replay) await client.Streams.ReadGroupOnceAsync("stale", "workers", "stable-0");
        var state = new ParkedDelivery();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IRespireClient>(client).AddSingleton(state);
        builder.Services.AddRespireStreamWorker<ParkedHandler>("stale", "workers", new()
        {
            ConsumerName = "stable", RecoveryPollInterval = TimeSpan.FromMinutes(1),
        });
        using var host = builder.Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(deadline.Token);
        try
        {
            await state.Entered.Task.WaitAsync(deadline.Token);
            var owner = sameConsumer ? "stable-0" : "competitor";
            using var claim = await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["stale"],
                ["workers", owner, 0, "0-0", 1], deadline.Token);
            var expectedAttempt = replay ? 3L : 2L;
            await Assert.That(claim[1][0][2].AsString()).IsEqualTo(expectedAttempt.ToString());
            state.Release.TrySetResult();
            await host.StopAsync(deadline.Token); // The stale handler returns Ack while draining.
            var pending = (await client.Streams.PendingAsync("stale", "workers")).Single();
            await Assert.That(pending.Consumer).IsEqualTo(owner);
            await Assert.That(pending.DeliveryCount).IsEqualTo(expectedAttempt);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Ack, ["stale"],
                ["workers", owner, "1-0", expectedAttempt], deadline.Token)).IsEqualTo(1);
        }
        finally
        {
            state.Release.TrySetResult();
            await host.StopAsync(deadline.Token);
        }
    }

    public sealed class ParkedDelivery
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ParkedHandler(ParkedDelivery state) : IRespireStreamHandler<RespireStreamEntry>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry entry, CancellationToken token)
        {
            state.Entered.TrySetResult();
            await state.Release.Task.WaitAsync(token);
            return RespireStreamWorkerResult.Ack;
        }
    }

    public sealed class RecoveredHandler(Channel<RespireStreamEntry> entered) : IRespireStreamHandler<RespireStreamEntry>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry entry, CancellationToken token)
        {
            entered.Writer.TryWrite(entry);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }
}
