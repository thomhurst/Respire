using System.Diagnostics;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class SentinelRoutingTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    public async Task TrackedReadRetriesGenerationRetiredBeforeAdmission(bool coalesce, bool cancel, bool retireReplacement)
    {
        static byte[]? Reply(int _, string command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" => "$3\r\nnew\r\n"u8.ToArray(),
            _ => null,
        };
        await using var first = Primary(Reply);
        await using var replacement = Primary(Reply);
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var original = client.Core.Sentinel!.Current!;
        var retirements = 0;
        var initialFlushes = client.ClientSideCache!.GetStatistics().ContinuityFlushes;
        Task<string?>? follower = null;
        using var cancellation = new CancellationTokenSource();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name == "GET"
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "GET" || !TestTelemetry.IsFrom(activity, first.Port, replacement.Port)) return;
                if (retirements != 0 && !retireReplacement) return;
                if (coalesce && retirements == 0)
                    follower = client.GetStringAsync("key").AsTask();
                retirements++;
                Volatile.Write(ref port, replacement.Port);
                // Force the admission race after SendTrackedAsync has selected its connection.
                var generation = client.Core.Sentinel.Current!;
                var connection = generation.Multiplexer.GetConnection();
                using var rejection = Respire.Protocol.RespValue.Error("READONLY demoted");
                generation.ObserveResponse(connection, "GET", in rejection);
                if (cancel) cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);

        var read = client.GetStringAsync("key", cancellation.Token).AsTask();
        if (cancel || retireReplacement)
        {
            await Assert.That(async () => await read.WaitAsync(Limit)).Throws<RespireConnectionRetiredException>();
            await Assert.That(retirements).IsEqualTo(retireReplacement ? 2 : 1);
            await Assert.That(replacement.ReceivedCommands).DoesNotContain("GET key");
            return;
        }
        await Assert.That(await read.WaitAsync(Limit)).IsEqualTo("new");
        if (coalesce)
        {
            await Assert.That(follower is not null).IsTrue();
            await Assert.That(await follower!.WaitAsync(Limit)).IsEqualTo("new");
        }
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes).IsEqualTo(initialFlushes + 1);
        await Assert.That(retirements).IsEqualTo(1);
        await Assert.That(original.IsRetired).IsTrue();
        await Assert.That(first.ReceivedCommands).DoesNotContain("GET key");
        await Assert.That(first.ReceivedCommands).DoesNotContain("CLIENT CACHING YES");
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(1);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }
}
