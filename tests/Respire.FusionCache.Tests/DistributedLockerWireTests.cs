using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.FusionCache.Tests;

public class DistributedLockerWireTests
{
    [Test]
    public async Task RenewalErrorStopsCleanupAndReportsUncertainOwnership()
    {
        await using var server = new FakeRespServer("$1\r\n1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SET ", StringComparison.Ordinal)
                ? "-NOPERM renewal denied\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client,
            new() { LeaseDuration = TimeSpan.FromSeconds(1) });
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!owner.OwnershipCancellationToken.IsCancellationRequested) await Task.Delay(10, timeout.Token);
        await owner.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        await Assert.That(owner.OwnershipLost).IsTrue();
        await Assert.That(owner.RenewalFailure).IsTypeOf<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(0);
        // After loss, cleanup never assumes ownership; server expiry is the fallback.
    }

    [Test]
    public async Task CancellationAfterSuccessfulReplyReleasesUnreturnedLeaseWithoutStartingRenewal()
    {
        await using var server = new FakeRespServer("$1\r\n1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client);
        using var cancellation = new CancellationTokenSource();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") is "EVALSHA"
                    && activity.GetTagItem("server.port") is int port && port == server.Port)
                    cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);
        var error = await Assert.That(async () => await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero, cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        var commands = server.ReceivedCommands;
        await Assert.That(commands.Count).IsEqualTo(4);
        var arguments = server.ReceivedArguments;
        // Managed release fences uncertainty before deleting only the acquired owner.
        await Assert.That(commands[1]).IsEqualTo("CLIENT ID");
        await Assert.That(commands[2]).IsEqualTo("CLIENT KILL ID 1 SKIPME yes");
        await Assert.That(arguments[3][1].SequenceEqual(arguments[0][3])).IsTrue();
        await Assert.That(arguments[3][3].SequenceEqual(arguments[0][5])).IsTrue();
    }
}
