using Respire.Internal;
using Respire.Testing;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class StreamWorkerTests
{
    [Test]
    [Arguments(1, false)]
    [Arguments(3, false)]
    [Arguments(1, true)]
    [Arguments(3, true)]
    public async Task NativeNackFreesReadersWithoutRetryingYoungFailures(int consumers, bool throws)
    {
        var clock = new RespireFakeClock(DateTimeOffset.UtcNow);
        var state = new State { Handle = (entry, _) =>
        {
            if (int.Parse(entry.GetString("payload")!) >= consumers)
                return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
            if (throws) throw new InvalidOperationException("downstream unavailable");
            return ValueTask.FromResult(RespireStreamWorkerResult.Nack);
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, clock: clock, workerVersion: new Version(8, 8),
            options: new() { ConsumerCount = consumers, BatchSize = 1,
                MinimumIdleTime = TimeSpan.FromSeconds(30), RecoveryPollInterval = TimeSpan.FromMilliseconds(10) });
        for (var i = 0; i < consumers; i++) await fixture.AddAsync(i);
        await fixture.StartAsync();
        for (var i = 0; i < consumers; i++)
            await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        for (var i = consumers; i < consumers * 2; i++) await fixture.AddAsync(i);
        for (var i = 0; i < consumers; i++)
        {
            var success = await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await Assert.That(int.Parse(success.GetString("payload")!)).IsGreaterThanOrEqualTo(consumers);
        }
        await UntilAsync(async () => (await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count == consumers);
        await fixture.StopAsync();
        await Assert.That(state.ScopeIds.Count).IsEqualTo(consumers * 2);
        var pending = await fixture.View.Streams.PendingAsync("events", "workers");
        foreach (var delivery in pending)
        {
            await Assert.That(delivery.DeliveryCount).IsEqualTo(1);
            await Assert.That(delivery.Consumer).IsNotEqualTo("");
        }
    }

    [Test]
    [Arguments("nack", false)]
    [Arguments("nack", true)]
    [Arguments("ack-delete", false)]
    [Arguments("ack-delete", true)]
    public async Task CapabilityCompletionFailureDoesNotRepeatOrFallback(string completion, bool afterExecution)
    {
        var nack = completion == "nack";
        var state = new State { Handle = (_, _) => ValueTask.FromResult(nack ? RespireStreamWorkerResult.Nack : RespireStreamWorkerResult.Ack) };
        await using var fixture = await Fixture.CreateAsync(state: state, workerVersion: new Version(8, 8), options: new()
        {
            DeleteAcknowledgedEntries = true, MinimumIdleTime = TimeSpan.FromMilliseconds(1),
        });
        var source = nack ? StreamWorkerScripts.NackSource : StreamWorkerScripts.AckAndDeleteSource;
        using var fault = fixture.Server.InjectFault("EVAL", RespireFakeFault.Disconnect(afterExecution),
            firstArgument: Encoding.UTF8.GetBytes(source));
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline)).Throws<Exception>();
        await using var control = await RespireClient.ConnectAsync(fixture.Server.CreateOptions());
        await Assert.That((await control.Streams.PendingSummaryAsync("events", "workers")).Count)
            .IsEqualTo(!nack && afterExecution ? 0 : 1);
        await Assert.That(state.ScopeIds.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(2, 0)]
    [Arguments(3, 2)]
    [Arguments(2, 4)]
    [Arguments(3, 8)]
    public async Task CapabilityRecoveryHonorsDeliveryLimitAndPrefixes(int protocol, int minor)
    {
        var state = new State { Handle = (_, _) => ValueTask.FromResult(RespireStreamWorkerResult.Nack) };
        await using var fixture = await Fixture.CreateAsync(protocol, keyPrefix: "{worker}tenant:", state: state,
            workerVersion: new Version(8, minor), options: new()
            {
                MinimumIdleTime = TimeSpan.FromMilliseconds(40), RecoveryPollInterval = TimeSpan.FromMilliseconds(10),
                ReadWait = TimeSpan.FromMilliseconds(10), DeadLetterStream = "dlq", DeliveryLimit = 2,
            });
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await UntilAsync(async () => await fixture.View.Streams.CountAsync("dlq") == 1);
        await fixture.StopAsync();
        await Assert.That(state.ScopeIds.Count).IsEqualTo(2);
        var dead = (await fixture.View.Streams.ReadAsync("dlq")).Single();
        await Assert.That(dead.GetString("_respire.attempt")).IsEqualTo("2");
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
    }
}

public class StreamWorkerCapabilityScriptTests
{
    [Test]
    [Arguments(2, 0)]
    [Arguments(3, 4)]
    [Arguments(2, 8)]
    public async Task RecoveryRemovesDeletedPendingEntriesOnlyFromItsGroup(int protocol, int minor)
    {
        await using var fake = new RespireFakeServer(null, false, true, new Version(8, minor));
        await using var client = await RespireClient.ConnectAsync(fake.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await DeletedPendingScenarioAsync(client);
    }

    internal static async Task DeletedPendingScenarioAsync(RespireClient client)
    {
        await client.Streams.AddAsync("deleted", new StreamAddOptions { Id = "1-0" }, ("f", "deleted"));
        await client.Streams.AddAsync("deleted", new StreamAddOptions { Id = "2-0" }, ("f", "live"));
        await client.Streams.CreateGroupAsync("deleted", "g", RespireStreamId.Beginning);
        await client.Streams.CreateGroupAsync("deleted", "other", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("deleted", "g", "old", new StreamReadOptions { Count = 2 });
        await client.Streams.ReadGroupOnceAsync("deleted", "other", "other-owner", new StreamReadOptions { Count = 2 });
        await client.Streams.RemoveAsync("deleted", new RespireStreamId("1-0"));
        using (var reply = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["deleted"],
            ["g", "new", 0, "0-0", 2]))
        {
            await Assert.That(reply[1].Count).IsEqualTo(1);
            await Assert.That(reply[1][0][0].AsString()).IsEqualTo("2-0");
            await Assert.That(reply[1][0][2].AsString()).IsEqualTo("2");
        }
        await Assert.That((await client.Streams.PendingSummaryAsync("deleted", "g")).Count).IsEqualTo(1);
        await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Ack, ["deleted"], ["g", "new", "2-0", 2]);
        using (var reply = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["deleted"],
            ["g", "new", 0, "0-0", 2]))
            await Assert.That(reply[1].Count).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync("deleted", "g")).Count).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync("deleted", "other")).Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(4)]
    [Arguments(8)]
    public async Task NativeCleanupCursorPassesLiveEntriesAndResetsAfterDeletedEntries(int minor)
    {
        await using var fake = new RespireFakeServer(null, false, true, new Version(8, minor));
        await using var client = await RespireClient.ConnectAsync(fake.CreateOptions());
        await CleanupCursorScenarioAsync(client);
    }

    internal static async Task CleanupCursorScenarioAsync(RespireClient client)
    {
        for (var i = 1; i <= 3; i++)
            await client.Streams.AddAsync("cleanup-cursor", new StreamAddOptions { Id = $"{i}-0" }, ("f", "v"));
        await client.Streams.CreateGroupAsync("cleanup-cursor", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("cleanup-cursor", "g", "old", new StreamReadOptions { Count = 3 });
        await client.Streams.RemoveAsync("cleanup-cursor", new RespireStreamId("2-0"), new RespireStreamId("3-0"));
        var cursor = "0-0";
        foreach (var expected in new[] { "2-0", "3-0", "0-0" })
        {
            using var reply = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["cleanup-cursor"],
                ["g", "new", 0, cursor, 1]);
            cursor = reply[0].AsString();
            await Assert.That(cursor).IsEqualTo(expected);
        }
        var pending = await client.Streams.PendingAsync("cleanup-cursor", "g");
        await Assert.That(pending.Length).IsEqualTo(1);
        await Assert.That(pending[0].Id.Value).IsEqualTo("1-0");
    }

    [Test]
    [Arguments(2, 0)]
    [Arguments(3, 2)]
    [Arguments(2, 4)]
    [Arguments(3, 8)]
    public async Task NativeAndCompatibleScriptsPreserveOwnershipAndOtherGroups(int protocol, int minor)
    {
        var clock = new RespireFakeClock(DateTimeOffset.UtcNow);
        await using var fake = new RespireFakeServer(clock, false, true, new Version(8, minor));
        await using var root = await RespireClient.ConnectAsync(fake.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await CapabilityScenarioAsync(root, clock, minor);
    }

    internal static async Task CapabilityScenarioAsync(RespireClient root, RespireFakeClock? clock, int minor)
    {
        var client = root.WithKeyPrefix("tenant:");
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("f", "v"));
        await client.Streams.CreateGroupAsync("events", "g", RespireStreamId.Beginning);
        await client.Streams.CreateGroupAsync("events", "other", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("events", "g", "old");
        using (var claimed = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["events"],
            ["g", "new", 0, "0-0", 1]))
        {
            await Assert.That(claimed[1][0][0].AsString()).IsEqualTo("1-0");
            await Assert.That(claimed[1][0][2].AsString()).IsEqualTo("2");
        }
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Nack, ["events"],
            ["g", "old", "1-0", 1, 0])).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingAsync("events", "g", RespireStreamId.Min, RespireStreamId.Max, 1))[0].Consumer)
            .IsEqualTo("new");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Nack, ["events"],
            ["g", "new", "1-0", 2, 60000])).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingAsync("events", "g", RespireStreamId.Min, RespireStreamId.Max, 1))[0].Consumer)
            .IsEqualTo("new");
        clock?.Advance(TimeSpan.FromMinutes(1));
        await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Nack, ["events"], ["g", "new", "1-0", 2, 0]);
        var pending = (await client.Streams.PendingAsync("events", "g", RespireStreamId.Min, RespireStreamId.Max, 1)).Single();
        await Assert.That(pending.Consumer).IsEqualTo(minor >= 8 ? "" : "new");
        await Assert.That(pending.DeliveryCount).IsEqualTo(2);
        using (var claimed = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["events"],
            ["g", "latest", 0, "0-0", 1]))
            await Assert.That(claimed[1][0][2].AsString()).IsEqualTo("3");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["events"],
            ["g", "new", "1-0", 2])).IsEqualTo(0);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["events"],
            ["g", "latest", "1-0", 3])).IsEqualTo(1);
        // The other group has not read this entry: ACKED must retain its body.
        await Assert.That(await client.Streams.CountAsync("events")).IsEqualTo(1);
        await client.Streams.ReadGroupOnceAsync("events", "other", "second");
        await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["events"],
            ["other", "second", "1-0", 1]);
        await Assert.That(await client.Streams.CountAsync("events")).IsEqualTo(minor >= 2 ? 0 : 1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    public async Task ClaimMetadataAndNewEntriesUseDifferentAttemptCounts(int minor)
    {
        await using var fake = new RespireFakeServer(null, false, true, new Version(8, minor));
        await using var client = await RespireClient.ConnectAsync(fake.CreateOptions());
        await ClaimMixScenarioAsync(client, minor);
    }

    internal static async Task ClaimMixScenarioAsync(RespireClient client, int minor)
    {
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("f", "pending"));
        await client.Streams.CreateGroupAsync("events", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("events", "g", "old");
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "2-0" }, ("f", "new"));
        using var reply = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["events"],
            ["g", "new", 0, "0-0", 2]);
        await Assert.That(reply[1].Count).IsEqualTo(minor >= 4 ? 2 : 1);
        await Assert.That(reply[1][0][2].AsString()).IsEqualTo("2");
        if (minor >= 4) await Assert.That(reply[1][1][2].AsString()).IsEqualTo("1");
        // Reclaiming under the same name still advances the attempt token.
        using var next = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["events"],
            ["g", "new", 0, "0-0", 2]);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["events"],
            ["g", "new", "1-0", 2])).IsEqualTo(0);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["events"],
            ["g", "new", "1-0", 3])).IsEqualTo(1);
    }
}
