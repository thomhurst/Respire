using Respire.Internal;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<Redis62VerbatimReplication>(Shared = SharedType.PerTestSession)]
public class StreamWorkerVerbatimReplicationTests(Redis62VerbatimReplication fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IdleRecoveryClaimsPendingDelivery(bool discoverCapabilities)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string stream = "idle-recovery";
        await DeliverAsync(client, stream);

        var script = discoverCapabilities ? StreamWorkerScripts.CapabilityClaim : StreamWorkerScripts.Claim;
        using var reply = await client.Scripts.ExecuteAsync(script, [stream], ["g", "successor", 0, "0-0", 1]);

        await Assert.That(reply[1][0][0].AsString()).IsEqualTo("1-0");
        await Assert.That(reply[1][0][2].AsString()).IsEqualTo("2");
        var pending = await client.Streams.PendingAsync(stream, "g", count: 1);
        await Assert.That(pending[0].Consumer).IsEqualTo("successor");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcknowledgementClearsPendingDelivery(bool delete)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string stream = "acknowledgement";
        await DeliverAsync(client, stream);
        var script = delete ? StreamWorkerScripts.AckAndDelete : StreamWorkerScripts.Ack;

        long acknowledged = await client.Scripts.ExecuteIntegerAsync(script, [stream], ["g", "owner", "1-0", 1]);

        await Assert.That(acknowledged).IsEqualTo(1L);
        await Assert.That((await client.Streams.PendingSummaryAsync(stream, "g")).Count).IsEqualTo(0);
        // Redis 6.2 falls back to XACK when delete is requested, preserving the stream entry.
        await Assert.That(await client.Streams.CountAsync(stream)).IsEqualTo(1);
    }

    [Test]
    public async Task RateLimitedRecoveryClaimsPendingDelivery()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string stream = "controlled-recovery";
        const string control = stream + ":control";
        await DeliverAsync(client, stream);
        await client.Scripts.ExecuteIntegerAsync(JobQueueControlTests.ControlScript("Configure"), [control], [1, 60_000, "set"]);

        var script = JobQueueControlTests.ControlScript("Claim");
        RespireKey[] keys = [stream, control, stream + ":dedup-deliveries"];
        RespireValue[] args = ["g", "successor", 0, "0-0", 1];
        using var claimed = await client.Scripts.ExecuteAsync(script, keys, args);

        await Assert.That(claimed[1][0][0].AsString()).IsEqualTo("1-0");
        await Assert.That(claimed[1][0][2].AsString()).IsEqualTo("2");
        var pending = await client.Streams.PendingAsync(stream, "g", count: 1);
        await Assert.That(pending[0].Consumer).IsEqualTo("successor");

        using var limited = await client.Scripts.ExecuteAsync(script, keys, args);
        await Assert.That(limited[1].Count).IsEqualTo(0);
        await Assert.That(limited[3].AsInteger()).IsGreaterThan(0);
    }

    private static async Task DeliverAsync(RespireClient client, string stream)
    {
        await client.Streams.AddAsync(stream, new RespireStreamAddOptions { Id = "1-0" }, ("f", "v"));
        await client.Streams.CreateGroupAsync(stream, "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync(stream, "g", "owner");
    }
}
