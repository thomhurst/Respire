using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class KeySerializationIntegrationTests(RedisTestContainer fixture)
{
    public enum Mode { Immediate, Batch, Transaction }

    [Test]
    [Arguments(2, Mode.Immediate)]
    [Arguments(2, Mode.Batch)]
    [Arguments(2, Mode.Transaction)]
    [Arguments(3, Mode.Immediate)]
    [Arguments(3, Mode.Batch)]
    [Arguments(3, Mode.Transaction)]
    public async Task BinaryAndListRoundTripsPreserveOwnedPayloads(int protocol, Mode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        var view = client.WithKeyPrefix($"dump:{Guid.NewGuid():N}:");
        byte[] bytes = [0, 255, 128, 13, 10, 1];
        await view.SetAsync("source", (RespireValue)bytes);
        var payload = await Run(view, mode, k => k.DumpAsync("source"), k => k.Dump("source"));
        payload.Should().NotBeNull();
        var original = payload!.ToArray();
        (await Run(view, mode, k => k.DumpAsync("missing"), k => k.Dump("missing"))).Should().BeNull();
        (await Run(view, mode, k => k.RestoreAsync("target", payload), k => k.Restore("target", payload))).Should().BeTrue();
        (await view.GetAsync<byte[]>("target")).Should().Equal(bytes);
        payload.Should().Equal(original); // Reply buffers and deferred containers have already been disposed.
        var another = await view.Keys.DumpAsync("source");
        payload[0] ^= 255;
        another.Should().Equal(original);
        (await client.Keys.ExistsAsync("target")).Should().BeFalse();

        await view.Lists.RightPushAsync("list", "a", "b");
        var listPayload = await Run(view, mode, k => k.DumpAsync("list"), k => k.Dump("list"));
        await Run(view, mode, k => k.RestoreAsync("list-copy", listPayload!), k => k.Restore("list-copy", listPayload!));
        (await view.Lists.RangeAsync("list-copy")).Should().Equal("a", "b");
    }

    [Test]
    [Arguments(2, Mode.Immediate)]
    [Arguments(2, Mode.Batch)]
    [Arguments(2, Mode.Transaction)]
    [Arguments(3, Mode.Immediate)]
    [Arguments(3, Mode.Batch)]
    [Arguments(3, Mode.Transaction)]
    public async Task ExpiryAndReplacementPreserveExplicitSemantics(int protocol, Mode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        var prefix = $"restore-expiry:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        await view.SetAsync("source", "new", expiry: TimeSpan.FromMinutes(1));
        var payload = (await view.Keys.DumpAsync("source"))!;
        await Run(view, mode, k => k.RestoreAsync("persistent", payload), k => k.Restore("persistent", payload));
        (await view.Keys.ExpiryAsync("persistent")).HasExpiry.Should().BeFalse();
        await view.SetAsync("replace", "old", expiry: TimeSpan.FromMinutes(1));
        var replace = new RespireRestoreOptions { Replace = true };
        await Run(view, mode, k => k.RestoreAsync("replace", payload, RespireExpiry.Persist, replace),
            k => k.Restore("replace", payload, RespireExpiry.Persist, replace));
        (await view.GetStringAsync("replace")).Should().Be("new");
        (await view.Keys.ExpiryAsync("replace")).HasExpiry.Should().BeFalse();
        await Run(view, mode, k => k.RestoreAsync("relative", payload, TimeSpan.FromMinutes(1)),
            k => k.Restore("relative", payload, TimeSpan.FromMinutes(1)));
        (await view.Keys.ExpiryAsync("relative")).TimeToLive!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        var instant = DateTimeOffset.UtcNow.AddMinutes(1);
        await Run(view, mode, k => k.RestoreAsync("absolute", payload, instant), k => k.Restore("absolute", payload, instant));
        using var at = await client.ExecuteAsync(RespireCommands.Key.PEXPIRETIME, prefix + "absolute");
        at.AsInteger().Should().Be(instant.ToUnixTimeMilliseconds());
        var past = DateTimeOffset.UnixEpoch.AddMilliseconds(1);
        await Run(view, mode, k => k.RestoreAsync("replace", payload, past, replace), k => k.Restore("replace", payload, past, replace));
        (await view.Keys.ExistsAsync("replace")).Should().BeFalse();
    }

    [Test]
    [Arguments(2, Mode.Immediate)]
    [Arguments(2, Mode.Batch)]
    [Arguments(2, Mode.Transaction)]
    [Arguments(3, Mode.Immediate)]
    [Arguments(3, Mode.Batch)]
    [Arguments(3, Mode.Transaction)]
    public async Task IdleTimeAndServerErrorsArePreserved(int protocol, Mode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        var prefix = $"restore-errors:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        await view.SetAsync("source", "a non-integer value");
        var payload = (await view.Keys.DumpAsync("source"))!;
        var idle = new RespireRestoreOptions { IdleTimeSeconds = 120 };
        await Run(view, mode, k => k.RestoreAsync("idle", payload, options: idle), k => k.Restore("idle", payload, options: idle));
        using var observed = await client.ExecuteAsync(RespireCommands.Key.OBJECT_IDLETIME, prefix + "idle");
        observed.AsInteger().Should().BeGreaterThanOrEqualTo(120).And.BeLessThan(150);
        Func<Task> busy = async () => { await Run(view, mode, k => k.RestoreAsync("source", payload), k => k.Restore("source", payload)); };
        (await busy.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("BUSYKEY");
        var corrupt = payload.ToArray();
        corrupt[^1] ^= 255;
        var unsupported = payload.ToArray();
        unsupported[^10] = unsupported[^9] = 255;
        foreach (var bad in new[] { Array.Empty<byte>(), corrupt, unsupported })
        {
            Func<Task> invalid = async () => { await Run(view, mode, k => k.RestoreAsync("bad", bad), k => k.Restore("bad", bad)); };
            (await invalid.Should().ThrowAsync<RespireServerException>()).Which.Message.Should().Contain("version or checksum");
        }
        (await view.Keys.ExistsAsync("bad")).Should().BeFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FrequencyRoundTripsUnderLfuWithoutChangingSharedServer(int protocol)
    {
        await using var redis = new RedisBuilder("redis:7.0.15")
            .WithCommand("redis-server", "--maxmemory-policy", "allkeys-lfu", "--lfu-decay-time", "0").Build();
        await redis.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(redis.Hostname, redis.GetMappedPublicPort(6379)) },
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3, Connections = 1
        });
        await client.SetAsync("source", "a non-integer value");
        var payload = (await client.Keys.DumpAsync("source"))!;
        foreach (var mode in Enum.GetValues<Mode>())
        foreach (byte frequency in new byte[] { 0, 255 })
        {
            var key = $"target:{mode}:{frequency}";
            var options = new RespireRestoreOptions { Frequency = frequency };
            await Run(client, mode, k => k.RestoreAsync(key, payload, options: options), k => k.Restore(key, payload, options: options));
            using var observed = await client.ExecuteAsync(RespireCommands.Key.OBJECT_FREQ, key);
            observed.AsInteger().Should().Be(frequency);
        }
    }

    private static async Task<T> Run<T>(IRespireClient client, Mode mode,
        Func<IKeyCommands, ValueTask<T>> immediate, Func<IBatchKeyCommands, RespirePending<T>> deferred)
    {
        if (mode == Mode.Immediate) return await immediate(client.Keys);
        if (mode == Mode.Batch)
        {
            using var batch = client.CreateBatch();
            var result = deferred(batch.Keys);
            await batch.TryExecuteAsync();
            return result.Result;
        }
        await using var transaction = client.CreateTransaction();
        var pending = deferred(transaction.Keys);
        await transaction.CommitAsync();
        return pending.Result;
    }
}
