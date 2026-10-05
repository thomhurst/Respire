using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class CopyDatabaseIntegrationTests(RedisTestContainer fixture)
{
    public enum ExecutionMode { Immediate, Batch, Transaction }

    [Test]
    [Arguments(2, ExecutionMode.Immediate)]
    [Arguments(2, ExecutionMode.Batch)]
    [Arguments(2, ExecutionMode.Transaction)]
    [Arguments(3, ExecutionMode.Immediate)]
    [Arguments(3, ExecutionMode.Batch)]
    [Arguments(3, ExecutionMode.Transaction)]
    public async Task CopyTargetsRequestedDatabaseWithoutChangingSource(int protocol, ExecutionMode mode)
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol };
        var destinationDatabase = (options.Database + 1) % 4096;
        await using var source = await RespireClient.ConnectAsync(options);
        await using var destination = await RespireClient.ConnectAsync(options with { Database = destinationDatabase });
        var prefix = $"copy-db:{Guid.NewGuid():N}:";
        var sourceView = source.WithKeyPrefix(prefix);
        var destinationView = destination.WithKeyPrefix(prefix);
        await sourceView.SetAsync("source", "original");
        await sourceView.SetAsync("target", "source-database-target");

        (await Copy(sourceView, mode, "missing", "absent", destinationDatabase)).Should().BeFalse();
        (await destinationView.GetStringAsync("absent")).Should().BeNull();
        (await Copy(sourceView, mode, "source", "target", destinationDatabase)).Should().BeTrue();
        (await destinationView.GetStringAsync("target")).Should().Be("original");
        (await sourceView.GetStringAsync("target")).Should().Be("source-database-target");
        (await destinationView.GetStringAsync("source")).Should().BeNull();

        await sourceView.SetAsync("source", "updated");
        (await Copy(sourceView, mode, "source", "target", destinationDatabase)).Should().BeFalse();
        (await destinationView.GetStringAsync("target")).Should().Be("original");
        (await Copy(sourceView, mode, "source", "target", destinationDatabase, replace: true)).Should().BeTrue();
        (await destinationView.GetStringAsync("target")).Should().Be("updated");
        (await sourceView.GetStringAsync("source")).Should().Be("updated");
        (await sourceView.GetStringAsync("target")).Should().Be("source-database-target");

        // The same key name is valid when its destination database differs.
        (await Copy(sourceView, mode, "source", "source", destinationDatabase)).Should().BeTrue();
        (await destinationView.GetStringAsync("source")).Should().Be("updated");
    }

    [Test]
    public async Task OutOfRangeDestinationIsNotSilentlyIgnored()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = $"copy-db:invalid:{Guid.NewGuid():N}";
        await client.SetAsync(key, "value");
        Func<Task> copy = async () => await client.Keys.CopyAsync(key, key + ":target", int.MaxValue);
        await copy.Should().ThrowAsync<RespireServerException>();
        (await client.GetStringAsync(key + ":target")).Should().BeNull();
    }

    private static async Task<bool> Copy(IRespireClient client, ExecutionMode mode,
        RespireKey source, RespireKey destination, int database, bool replace = false)
    {
        if (mode == ExecutionMode.Immediate) return await client.Keys.CopyAsync(source, destination, database, replace);
        using var batch = mode == ExecutionMode.Batch ? client.CreateBatch() : null;
        await using var transaction = mode == ExecutionMode.Transaction ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Keys.Copy(source, destination, database, replace);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }
}
