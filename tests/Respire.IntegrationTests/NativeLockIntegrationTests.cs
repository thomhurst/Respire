using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// INFO commandstats is server-wide, so these rows use their own servers and run one at a time.
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.Keyed, Key = VersionedServerFixture.CommandStatsKey)]
[NotInParallel(VersionedServerFixture.CommandStatsKey)]
public class NativeLockIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments("redis:8.4-alpine", 2, true, "delex")]
    [Arguments("redis:8.4-alpine", 3, true, "delex")]
    [Arguments("valkey/valkey:8.1-alpine", 2, true, "evalsha")]
    [Arguments("valkey/valkey:8.1-alpine", 3, true, "evalsha")]
    [Arguments("valkey/valkey:9.0-alpine", 2, true, "delifeq")]
    [Arguments("valkey/valkey:9.0-alpine", 3, true, "delifeq")]
    [Arguments("redis:7.0.15", 2, false, "evalsha")]
    [Arguments("redis:7.0.15", 3, false, "evalsha")]
    public async Task VersionedLockOperationsPreserveOwnershipAndUseExpectedCommands(
        string image, int protocol, bool nativeExtension, string releaseCommand)
    {
        var server = await servers.LeaseAsync(image);
        await using var owner = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { server.Endpoint }, Database = server.Database,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            Connections = 1,
            ClientSideCache = protocol == 3 ? new() : null,
        });
        var client = owner.WithKeyPrefix("tenant:");
        RespireKey key = new byte[] { 0xff, 0, 0x42 };
        var token = new RespireLockToken(new byte[] { 0xfe, 0, 0xc3, 0x28 });
        var replacement = new RespireLockToken(new byte[] { 0xfd, 0, 0xc3, 0x28 });
        await VerifyOwnershipAndCommandSelectionAsync(owner, client, key, token, replacement, nativeExtension, releaseCommand);
        await VerifyExpiredOwnerAsync(client, key, token);
        await VerifyReplacementRaceAsync(client, key, token, replacement,
            server.ConnectionString(protocol));
        await VerifyWrongTypeAsync(client, key, token);
        await VerifyManagedLockAsync(client, key);
    }

    private static async Task VerifyOwnershipAndCommandSelectionAsync(
        RespireClient owner, IRespireClient client, RespireKey key, RespireLockToken token,
        RespireLockToken replacement, bool nativeExtension, string releaseCommand)
    {
        (await client.Locks.TryTakeAsync(key, token, TimeSpan.FromSeconds(30))).Should().BeTrue();
        (await client.Locks.GetOwnerTokenAsync(key) == token).Should().BeTrue();
        var before = await Stats(owner);
        (await client.Locks.ResetExpiryAsync(key, token, TimeSpan.FromSeconds(60))).Should().BeTrue();
        var after = await Stats(owner);
        // The first SET is also counted when an older server rejects its IFEQ option.
        Delta(before, after, "set").Should().Be(1);
        var extensionScripts = Delta(before, after, "evalsha") + Delta(before, after, "eval");
        if (nativeExtension) extensionScripts.Should().Be(0);
        else extensionScripts.Should().BeInRange(1, 2);
        (await client.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromSeconds(50));
        before = after;
        (await client.Locks.ResetExpiryAsync(key, replacement, TimeSpan.FromSeconds(1))).Should().BeFalse();
        after = await Stats(owner);
        Delta(before, after, "set").Should().Be(nativeExtension ? 1 : 0);
        (await client.Locks.ReleaseAsync(key, replacement)).Should().BeFalse();
        (await client.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromSeconds(50));
        before = await Stats(owner);
        (await client.Locks.ReleaseAsync(key, token)).Should().BeTrue();
        after = await Stats(owner);
        if (releaseCommand == "evalsha")
            (Delta(before, after, "evalsha") + Delta(before, after, "eval")).Should().Be(1);
        else
        {
            Delta(before, after, releaseCommand).Should().Be(1);
            (Delta(before, after, "evalsha") + Delta(before, after, "eval")).Should().Be(0);
        }
        (await client.Locks.GetOwnerTokenAsync(key)).Should().BeNull();
        (await client.Locks.ResetExpiryAsync(key, token, TimeSpan.FromSeconds(60))).Should().BeFalse();
        (await client.Locks.ReleaseAsync(key, token)).Should().BeFalse();
        (await client.Keys.ExistsAsync(key)).Should().BeFalse();
    }

    private static async Task VerifyExpiredOwnerAsync(IRespireClient client, RespireKey key, RespireLockToken token)
    {
        (await client.Locks.TryTakeAsync(key, token, TimeSpan.FromMilliseconds(1))).Should().BeTrue();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await client.Keys.ExistsAsync(key, deadline.Token)) await Task.Delay(10, deadline.Token);
        (await client.Locks.ResetExpiryAsync(key, token, TimeSpan.FromSeconds(60))).Should().BeFalse();
        (await client.Locks.ReleaseAsync(key, token)).Should().BeFalse();
    }

    private static async Task VerifyReplacementRaceAsync(
        IRespireClient client, RespireKey key, RespireLockToken token, RespireLockToken replacement, string connectionString)
    {
        await using var replacementClient = await RespireClient.ConnectAsync(connectionString);
        var replacementView = replacementClient.WithKeyPrefix("tenant:");

        // A replacement races the old owner's release or renewal. Either execution order
        // must leave the replacement present with its own TTL and exact binary token.
        for (var i = 0; i < 20; i++)
        {
            await client.SetAsync(key, (RespireValue)token.Bytes, expiry: TimeSpan.FromSeconds(30));
            var stale = i % 2 == 0
                ? client.Locks.ReleaseAsync(key, token).AsTask()
                : client.Locks.ResetExpiryAsync(key, token, TimeSpan.FromMinutes(5)).AsTask();
            await replacementView.SetAsync(key, (RespireValue)replacement.Bytes, expiry: TimeSpan.FromSeconds(20));
            await stale;
            (await replacementView.Locks.GetOwnerTokenAsync(key) == replacement).Should().BeTrue();
            (await replacementView.Keys.ExpiryAsync(key)).TimeToLive.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(20));
        }
        await client.Keys.DeleteAsync(key);
    }

    private static async Task VerifyWrongTypeAsync(IRespireClient client, RespireKey key, RespireLockToken token)
    {
        await client.Lists.RightPushAsync(key, "wrong-type");
        Func<Task> releaseWrongType = async () => { await client.Locks.ReleaseAsync(key, token); };
        Func<Task> extendWrongType = async () => { await client.Locks.ResetExpiryAsync(key, token, TimeSpan.FromSeconds(5)); };
        await releaseWrongType.Should().ThrowAsync<RespireServerException>();
        await extendWrongType.Should().ThrowAsync<RespireServerException>();
        await client.Keys.DeleteAsync(key);
    }

    private static async Task VerifyManagedLockAsync(IRespireClient client, RespireKey key)
    {
        await using var mutex = await client.Locks.AcquireOrThrowAsync(key, TimeSpan.FromSeconds(30));
        (await mutex.ResetExpiryAsync(TimeSpan.FromSeconds(60))).Should().BeTrue();
        (await mutex.VerifyStillHeldAsync()).Should().BeTrue();
        (await mutex.ReleaseAsync()).Should().Be(LockReleaseOutcome.Released);
    }

    private static async Task<Dictionary<string, long>> Stats(RespireClient client)
    {
        using var result = await client.ExecuteAsync("INFO", "commandstats");
        return result.AsString().Split('\n').Where(line => line.StartsWith("cmdstat_", StringComparison.Ordinal))
            .ToDictionary(line => line[8..line.IndexOf(':')],
                line => long.Parse(line[(line.IndexOf("calls=", StringComparison.Ordinal) + 6)..line.IndexOf(',')]));
    }

    private static long Delta(Dictionary<string, long> before, Dictionary<string, long> after, string command)
        => after.GetValueOrDefault(command) - before.GetValueOrDefault(command);
}
