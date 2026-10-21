using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class TrackedReplicaRedirectTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CancelledCoordinationAcquireFencesActualTarget(bool clientCache, bool hashLease)
    {
        await using var fixture = new ReplicaRedirectTestFixture(sentinel: false, clientCache);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Target.SuppressReply = command =>
        {
            if (!command.StartsWith("EVAL ")) return false;
            submitted.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(fixture.Options);
        var pools = client.Core.OwnedPools.Count;
        using var caller = new CancellationTokenSource();
        async Task Acquire()
        {
            if (hashLease)
            {
                await using var lease = await new RespireCoordination(client)
                    .TryAcquireLeaseAsync("key", "field", TimeSpan.FromMinutes(1), caller.Token);
            }
            else
            {
                await using var attempt = await new RespireSemaphore(client, "key", 1)
                    .TryAcquireAsync(TimeSpan.FromMinutes(1), caller.Token);
            }
        }
        var pending = Acquire();
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Target.SuppressReply = null;
        caller.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(fixture.Target.ReceivedCommands.Contains("CLIENT KILL ID 99")).IsTrue();
        await Assert.That(fixture.Source.ReceivedCommands.Contains("CLIENT KILL ID 41")).IsFalse();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SemaphoreAcquireAndRenewReleaseRedirectTargets(bool sentinel, bool clientCache)
    {
        await using var fixture = new ReplicaRedirectTestFixture(sentinel, clientCache);
        await using var client = await RespireClient.ConnectAsync(fixture.Options);
        var pools = client.Core.OwnedPools.Count;
        await using var attempt = await new RespireSemaphore(client, "key", 1).TryAcquireAsync(TimeSpan.FromMinutes(1));
        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        await Assert.That(await attempt.Permit.ResetExpiryAsync(TimeSpan.FromMinutes(1))).IsTrue();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        await Assert.That(fixture.Target.ReceivedCommands.Count(command => command.StartsWith("EVAL "))).IsEqualTo(2);
        await Assert.That(fixture.Source.ReceivedCommands.Count(command => command.StartsWith("EVAL "))).IsEqualTo(0);
    }
}
