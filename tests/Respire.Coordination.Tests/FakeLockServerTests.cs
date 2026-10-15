using System.Globalization;
using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class FakeLockServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ManagedLeaseCanVerifyRenewAndRelease(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await using var lease = await client.Locks.AcquireOrThrowAsync("lock", TimeSpan.FromMinutes(1));
        await Assert.That(await lease.VerifyStillHeldAsync()).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(40));
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromMinutes(1))).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(40));
        await Assert.That(await lease.VerifyStillHeldAsync()).IsTrue();
        await lease.ReleaseAsync();
        await Assert.That(await client.GetStringAsync("lock")).IsNull();
    }

    [Test]
    public async Task ExpiredOwnerCannotRenewOrReleaseReplacement()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await Assert.That(await client.Locks.TryTakeAsync("lock", "first", TimeSpan.FromSeconds(30))).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.That(await client.Locks.TryTakeAsync("lock", "second", TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(await client.Locks.ResetExpiryAsync("lock", "first", TimeSpan.FromMinutes(1))).IsFalse();
        await Assert.That(await client.Locks.ReleaseAsync("lock", "first")).IsFalse();
        await Assert.That(await client.GetStringAsync("lock")).IsEqualTo("second");
        clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.That(await client.GetStringAsync("lock")).IsNull();
    }

    [Test]
    public async Task ScriptsRequireExactShapeAndSourceAndCacheKnownHashes()
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var set = await session.CommandAsync("SET", "lock", "owner")) { }
        using (var missing = await session.CommandAsync("EVALSHA", LockCommands.ReleaseScript.Sha1, "1", "lock", "owner"))
            await Assert.That(missing.GetErrorMessage()).StartsWith("NOSCRIPT");
        using (var wrongKeys = await session.CommandAsync("EVAL", LockCommands.ReleaseScript.Source, "0", "lock", "owner"))
            await Assert.That(wrongKeys.IsError).IsTrue();
        using (var missingOwner = await session.CommandAsync("EVAL", LockCommands.ReleaseScript.Source, "1", "lock"))
            await Assert.That(missingOwner.IsError).IsTrue();
        using (var altered = await session.CommandAsync("EVAL", LockCommands.ReleaseScript.Source + "\n-- changed", "1", "lock", "owner"))
            await Assert.That(altered.IsError).IsTrue();
        using (var retained = await session.CommandAsync("GET", "lock"))
            await Assert.That(retained.AsString()).IsEqualTo("owner");
        using (var wrongOwner = await session.CommandAsync("EVAL", LockCommands.ReleaseScript.Source, "1", "lock", "other"))
            await Assert.That(wrongOwner.AsInteger()).IsEqualTo(0);
        using (var released = await session.CommandAsync("EVALSHA", LockCommands.ReleaseScript.Sha1, "1", "lock", "owner"))
            await Assert.That(released.AsInteger()).IsEqualTo(1);
    }

    [Test]
    public async Task ClientKillFencesPausedCommandAndSkipsCaller()
    {
        await using var server = new RespireFakeServer();
        await using var caller = await TestRespSession.ConnectAsync(server.CreateOptions());
        await using var victim = await TestRespSession.ConnectAsync(server.CreateOptions());
        using var callerIdentity = await caller.CommandAsync("CLIENT", "ID");
        using var victimIdentity = await victim.CommandAsync("CLIENT", "ID");
        var callerId = callerIdentity.AsInteger().ToString(CultureInfo.InvariantCulture);
        var victimId = victimIdentity.AsInteger().ToString(CultureInfo.InvariantCulture);
        using (var probe = await caller.CommandAsync("CLIENT", "KILL", "ID", callerId, "SKIPME", "yes"))
            await Assert.That(probe.AsInteger()).IsEqualTo(0);
        using (var invalid = await caller.CommandAsync("CLIENT", "KILL", "ID", victimId, "unexpected", "yes"))
            await Assert.That(invalid.IsError).IsTrue();

        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("SET", RespireFakeFault.Pause(gate));
        var pending = victim.CommandAsync("SET", "must-not-execute", "value");
        await fault.Matched.WaitAsync(TimeSpan.FromSeconds(5));
        using (var killed = await caller.CommandAsync("CLIENT", "KILL", "ID", victimId))
            await Assert.That(killed.AsInteger()).IsEqualTo(1);
        gate.Release();
        await Assert.That(async () => { using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5)); })
            .Throws<IOException>();
        await Assert.That(fault.ExecutionCount).IsEqualTo(0);
        using (var missing = await caller.CommandAsync("GET", "must-not-execute"))
            await Assert.That(missing.IsNull).IsTrue();
        using (var absent = await caller.CommandAsync("CLIENT", "KILL", "ID", victimId))
            await Assert.That(absent.AsInteger()).IsEqualTo(0);
    }
}
