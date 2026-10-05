using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class KeyMetadataIntegrationTests(RedisTestContainer fixture)
{
    [ClassDataSource<LfuRedisTestContainer>(Shared = SharedType.PerTestSession)]
    public required LfuRedisTestContainer Lfu { get; init; }

    public enum ExecutionMode { Immediate, Batch, Transaction }

    [Test]
    [Arguments(2, ExecutionMode.Immediate)]
    [Arguments(2, ExecutionMode.Batch)]
    [Arguments(2, ExecutionMode.Transaction)]
    [Arguments(3, ExecutionMode.Immediate)]
    [Arguments(3, ExecutionMode.Batch)]
    [Arguments(3, ExecutionMode.Transaction)]
    public async Task AbsoluteExpiryDistinguishesMissingPersistentAndPrecisions(int protocol, ExecutionMode mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant:");
        await view.SetAsync("persistent", "value");
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(2100000000678);
        await view.SetAsync("expiring", "value", expiry: instant);
        foreach (var precision in new[] { ExpiryTimePrecision.Milliseconds, ExpiryTimePrecision.Seconds })
        {
            var missing = await Run(view, mode, k => k.ExpiryTimeAsync("missing", precision), k => k.ExpiryTime("missing", precision));
            missing.Exists.Should().BeFalse();
            missing.HasExpiry.Should().BeFalse();
            var persistent = await Run(view, mode, k => k.ExpiryTimeAsync("persistent", precision), k => k.ExpiryTime("persistent", precision));
            persistent.Exists.Should().BeTrue();
            persistent.HasExpiry.Should().BeFalse();
            var expiring = await Run(view, mode, k => k.ExpiryTimeAsync("expiring", precision), k => k.ExpiryTime("expiring", precision));
            expiring.Exists.Should().BeTrue();
            expiring.UnixTimeMilliseconds.Should().Be(precision == ExpiryTimePrecision.Milliseconds ? 2100000000678L : 2100000001000L);
            expiring.GetExpiresAt().Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(expiring.UnixTimeMilliseconds!.Value));
        }
        (await client.Keys.ExistsAsync("expiring")).Should().BeFalse();
    }

    [Test]
    [Arguments(2, ExecutionMode.Immediate)]
    [Arguments(2, ExecutionMode.Batch)]
    [Arguments(2, ExecutionMode.Transaction)]
    [Arguments(3, ExecutionMode.Immediate)]
    [Arguments(3, ExecutionMode.Batch)]
    [Arguments(3, ExecutionMode.Transaction)]
    public async Task ObjectMetadataPreservesMissingKeysAndNonLfuPolicyErrors(int protocol, ExecutionMode mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant:");
        RespireKey key = new byte[] { 0xff, 0, (byte)'x' };
        await view.SetAsync(key, "not-an-integer");
        (await Run(view, mode, k => k.EncodingAsync(key), k => k.Encoding(key))).Should().Be("embstr");
        var idle = await Run(view, mode, k => k.IdleTimeAsync(key), k => k.IdleTime(key));
        idle.Should().NotBeNull();
        idle!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        (await Run(view, mode, k => k.ReferenceCountAsync(key), k => k.ReferenceCount(key))).Should().BeGreaterThan(0);
        (await Run(view, mode, k => k.EncodingAsync("missing"), k => k.Encoding("missing"))).Should().BeNull();
        (await Run(view, mode, k => k.IdleTimeAsync("missing"), k => k.IdleTime("missing"))).Should().BeNull();
        (await Run(view, mode, k => k.FrequencyAsync("missing"), k => k.Frequency("missing"))).Should().BeNull();
        (await Run(view, mode, k => k.ReferenceCountAsync("missing"), k => k.ReferenceCount("missing"))).Should().BeNull();
        Func<Task> frequency = async () => { await Run(view, mode, k => k.FrequencyAsync(key), k => k.Frequency(key)); };
        await frequency.Should().ThrowAsync<RespireServerException>();
    }

    [Test]
    [Arguments(2, ExecutionMode.Immediate)]
    [Arguments(2, ExecutionMode.Batch)]
    [Arguments(2, ExecutionMode.Transaction)]
    [Arguments(3, ExecutionMode.Immediate)]
    [Arguments(3, ExecutionMode.Batch)]
    [Arguments(3, ExecutionMode.Transaction)]
    public async Task LfuFrequencyAndIdlePolicyErrorArePreserved(int protocol, ExecutionMode mode)
    {
        // The eviction policy is server-wide, so this runs on the dedicated LFU server.
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(Lfu.ConnectionString) with { Protocol = (RespProtocol)protocol });
        await client.SetAsync("frequency", "value");
        var frequency = await Run(client, mode, k => k.FrequencyAsync("frequency"), k => k.Frequency("frequency"));
        frequency.Should().NotBeNull();
        frequency!.Value.Should().BeInRange(0, 255);
        (await Run(client, mode, k => k.FrequencyAsync("missing"), k => k.Frequency("missing"))).Should().BeNull();
        Func<Task> idle = async () => { await Run(client, mode, k => k.IdleTimeAsync("frequency"), k => k.IdleTime("frequency")); };
        await idle.Should().ThrowAsync<RespireServerException>();
    }

    private static async Task<T> Run<T>(IRespireClient client, ExecutionMode mode,
        Func<IKeyCommands, ValueTask<T>> immediate, Func<IBatchKeyCommands, RespirePending<T>> deferred)
    {
        if (mode == ExecutionMode.Immediate) return await immediate(client.Keys);
        using var batch = mode == ExecutionMode.Batch ? client.CreateBatch() : null;
        await using var transaction = mode == ExecutionMode.Transaction ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = deferred(queue.Keys);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.TryExecuteAsync();
        return pending.Result;
    }
}
