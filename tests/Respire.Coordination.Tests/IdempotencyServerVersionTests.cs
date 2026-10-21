using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public sealed class IdempotencyRedis62 : RedisTestContainer
{
    protected override string ServerImage => "redis:6.2.14-alpine";
}

[ClassDataSource<IdempotencyRedis62>(Shared = SharedType.PerTestSession)]
public class IdempotencyServerVersionTests(IdempotencyRedis62 fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MissingPermissionPreflightRejectsMutationsBeforeWriting(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Connections = 1, Protocol = (RespireProtocol)protocol,
        });
        var store = new RespireIdempotency(client);
        var key = Guid.NewGuid().ToString("N");
        var error = await Assert.That(async () => await store.TryBeginAsync(key, TimeSpan.FromMinutes(1)))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("Redis 7.0+");
        using (var exists = await client.ExecuteAsync("EXISTS", key))
            await Assert.That(exists.AsInteger()).IsEqualTo(0L);

        // An existing claim can still be inspected; an eligible completion must fail before writing.
        (await client.ExecuteAsync("HSET", key, "state", "pending", "owner", "owner", "fingerprint", "0", "ttl", "60000")).Dispose();
        await client.Keys.ExpireAsync(key, RespireExpiry.In(TimeSpan.FromMinutes(1)));
        await Assert.That((await store.TryBeginAsync(key, TimeSpan.FromMinutes(1))).State)
            .IsEqualTo(RespireIdempotencyState.InProgress);
        await Assert.That(await store.CompleteAsync(key, "wrong-owner", default)).IsFalse();
        error = await Assert.That(async () => await store.CompleteAsync(key, "owner", new byte[] { 1 }))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("Redis 7.0+");
        using (var state = await client.ExecuteAsync("HGET", key, "state"))
            await Assert.That(state.AsString()).IsEqualTo("pending");
        using (var owner = await client.ExecuteAsync("HGET", key, "owner"))
            await Assert.That(owner.AsString()).IsEqualTo("owner");
        using (var result = await client.ExecuteAsync("HGET", key, "result"))
            await Assert.That(result.IsNull).IsTrue();
    }
}
