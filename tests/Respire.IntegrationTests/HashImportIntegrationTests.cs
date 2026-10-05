using FluentAssertions;
using Respire.TestSupport;
using TUnit.Core;

namespace Respire.IntegrationTests;

public sealed class Redis810HashImportTestContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");

[ClassDataSource<Redis810HashImportTestContainer>(Shared = SharedType.PerTestSession)]
public class HashImportIntegrationTests(Redis810HashImportTestContainer fixture)
{
    [Test]
    [Arguments(false, 2, "immediate")]
    [Arguments(false, 3, "immediate")]
    [Arguments(false, 2, "batch")]
    [Arguments(false, 3, "batch")]
    [Arguments(false, 2, "transaction")]
    [Arguments(false, 3, "transaction")]
    [Arguments(true, 2, "immediate")]
    [Arguments(true, 3, "immediate")]
    [Arguments(true, 2, "batch")]
    [Arguments(true, 3, "batch")]
    [Arguments(true, 2, "transaction")]
    [Arguments(true, 3, "transaction")]
    public async Task ImportOverwritesHashAndTtlWithOriginalFieldOrder(bool useFake, int protocol, string mode)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var root = await RespireClient.ConnectAsync(Options(fake, protocol));
        var client = root.WithKeyPrefix($"import:{Guid.NewGuid():N}:");
        await client.Hashes.SetAsync("key", "old", "old");
        (await client.Keys.ExpireAsync("key", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await client.Keys.ExpiryAsync("key")).HasExpiry.Should().BeTrue();
        await using var session = await client.Hashes.CreateImportSessionAsync();
        byte[] binary = [0, 128, 255];
        if (mode == "immediate")
        {
            (await session.PrepareAsync("schema", "z", "a")).Should().BeTrue();
            (await session.SetAsync("key", "schema", "z-value", binary)).Should().BeTrue();
            (await session.PrepareAsync("second", "other")).Should().BeTrue();
            (await session.DiscardAsync("schema")).Should().BeTrue();
            (await session.DiscardAllAsync()).Should().Be(1);
        }
        else
        {
            using var batch = mode == "batch" ? session.CreateBatch() : null;
            await using var multi = mode == "transaction" ? session.CreateTransaction() : null;
            IRespireCommandQueue queue = multi ?? (IRespireCommandQueue)batch!;
            var prepared = queue.Hashes.PrepareImport("schema", "z", "a");
            var imported = queue.Hashes.Import("key", "schema", "z-value", binary);
            _ = queue.Hashes.PrepareImport("second", "other");
            var discarded = queue.Hashes.DiscardImport("schema");
            var cleared = queue.Hashes.DiscardAllImports();
            if (multi is not null) await multi.CommitAsync();
            else await batch!.ExecuteAsync();
            prepared.Result.Should().BeTrue();
            imported.Result.Should().BeTrue();
            discarded.Result.Should().BeTrue();
            cleared.Result.Should().Be(1);
        }
        (await client.Hashes.GetStringAsync("key", "z")).Should().Be("z-value");
        (await client.Hashes.GetStringAsync("key", "old")).Should().BeNull();
        (await client.Hashes.GetBytesAsync("key", "a")).Should().Equal(binary);
        var ttl = await client.Keys.ExpiryAsync("key");
        ttl.Exists.Should().BeTrue();
        ttl.HasExpiry.Should().BeFalse();
        (await session.DiscardAsync("schema")).Should().BeFalse();
        Func<Task> expired = async () => await session.SetAsync("key", "schema", "unused", "unused");
        await expired.Should().ThrowAsync<RespireServerException>();
        await session.PrepareAsync("replacement", "only");
        await session.SetAsync("key", "replacement", "new");
        (await client.Hashes.GetStringAsync("key", "z")).Should().BeNull();
        (await client.Hashes.GetStringAsync("key", "only")).Should().Be("new");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task RawValidationPreservesFieldsetAndConnectionIsolation(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = Options(fake, protocol);
        await using var wire = await TestRespSession.ConnectAsync(options);
        await using var other = await TestRespSession.ConnectAsync(options);
        var key = $"raw-import:{Guid.NewGuid():N}";
        using (var prepared = await wire.CommandAsync("HIMPORT", "PREPARE", "schema", "z", "a"))
            prepared.AsString().Should().Be("OK");
        string[][] invalid =
        [
            ["HIMPORT", "PREPARE", "schema", "z", "z"],
            ["HIMPORT", "PREPARE", "empty"],
            ["HIMPORT", "SET", key, "schema", "one"],
            ["HIMPORT", "SET", key, "unknown", "one"],
            ["HIMPORT", "DISCARD"], ["HIMPORT", "DISCARDALL", "extra"],
        ];
        foreach (var args in invalid)
        {
            using var rejected = await wire.CommandAsync(args);
            rejected.IsError.Should().BeTrue(string.Join(' ', args));
        }
        using (var isolated = await other.CommandAsync("HIMPORT", "SET", key, "schema", "one", "two"))
            isolated.IsError.Should().BeTrue();
        using (var imported = await wire.CommandAsync("HIMPORT", "SET", key, "schema", "one", "two"))
            imported.AsString().Should().Be("OK");
        using (var read = await wire.CommandAsync("HGET", key, "a")) read.AsString().Should().Be("two");
        using (var discarded = await wire.CommandAsync("HIMPORT", "DISCARD", "schema")) discarded.AsInteger().Should().Be(1);
        using (var absent = await wire.CommandAsync("HIMPORT", "DISCARD", "schema")) absent.AsInteger().Should().Be(0);
        using (var clear = await wire.CommandAsync("HIMPORT", "DISCARDALL")) clear.AsInteger().Should().Be(0);
        if (!useFake)
        {
            using (var prepared = await wire.CommandAsync("HIMPORT", "PREPARE", "reset", "field")) prepared.IsError.Should().BeFalse();
            using (var reset = await wire.CommandAsync("RESET")) reset.AsString().Should().Be("RESET");
            using var expired = await wire.CommandAsync("HIMPORT", "SET", key, "reset", "value");
            expired.IsError.Should().BeTrue();
        }
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task OnlyImportedKeysInvalidateWatch(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await RespireClient.ConnectAsync(Options(fake, protocol));
        var key = $"watch-import:{Guid.NewGuid():N}";
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await using (var unchanged = await client.CreateTransactionAsync([key]))
        {
            await session.PrepareAsync("schema", "field");
            await session.DiscardAsync("schema");
            await session.PrepareAsync("schema", "field");
            await session.DiscardAllAsync();
            _ = unchanged.GetString(key);
            (await unchanged.CommitAsync()).Should().BeTrue();
        }
        await session.PrepareAsync("schema", "field");
        await using var changed = await client.CreateTransactionAsync([key]);
        var pending = changed.Hashes.GetString(key, "field");
        await session.SetAsync(key, "schema", "value");
        (await changed.CommitAsync()).Should().BeFalse();
        pending.Status.Should().Be(RespirePendingStatus.Aborted);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImportInvalidatesCachedHashResults(bool transaction)
    {
        await using var client = await RespireClient.ConnectAsync(Options(null, 3) with { ClientSideCache = new() });
        var key = $"cache-import:{Guid.NewGuid():N}";
        await client.Hashes.SetAsync(key, "field", "old");
        (await client.Hashes.GetStringAsync(key, "field")).Should().Be("old");
        (await client.Hashes.GetStringAsync(key, "field")).Should().Be("old");
        client.ClientSideCache!.GetStatistics().Hits.Should().BeGreaterThan(0);
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        if (transaction)
        {
            await using var multi = session.CreateTransaction();
            _ = multi.Hashes.Import(key, "schema", "new");
            await multi.CommitAsync();
        }
        else await session.SetAsync(key, "schema", "new");
        (await client.Hashes.GetStringAsync(key, "field")).Should().Be("new");
    }

    private RespireOptions Options(RespireFakeServer? fake, int protocol)
        => (fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString)) with { Protocol = (RespProtocol)protocol };

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task WrongTypePreservesExistingValueAndTtlAndPrecedesFieldsetValidation(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await RespireClient.ConnectAsync(Options(fake, protocol));
        var key = $"wrong-import:{Guid.NewGuid():N}";
        await client.SetAsync(key, "old");
        await client.Keys.ExpireAsync(key, TimeSpan.FromMinutes(1));
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        foreach (var name in new[] { "schema", "unknown" })
        {
            Func<Task> import = async () => await session.SetAsync(key, name, "value");
            var error = await import.Should().ThrowAsync<RespireServerException>();
            error.Which.Code.Should().Be("WRONGTYPE");
        }
        (await client.GetStringAsync(key)).Should().Be("old");
        (await client.Keys.ExpiryAsync(key)).HasExpiry.Should().BeTrue();
        await session.SetAsync(key + ":valid", "schema", "value");
        (await client.Hashes.GetStringAsync(key + ":valid", "field")).Should().Be("value");
    }
}
