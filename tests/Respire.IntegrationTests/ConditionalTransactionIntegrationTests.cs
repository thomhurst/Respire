using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class ConditionalTransactionIntegrationTests(VersionedServerFixture servers)
{
    private const string Valkey = "valkey/valkey:9.2.0-rc1-alpine";

    [Test]
    [MatrixDataSource]
    public async Task ConditionsCommitOrAbortAtomically([Matrix(2, 3)] int protocol,
        [Matrix("IFEQ", "IFNE", "NX", "XX")] string token, [Matrix(true, false)] bool matches)
    {
        var server = await servers.LeaseAsync(Valkey);
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        var view = client.WithKeyPrefix("tenant:");
        byte[] value = [0, 255, 13, 10];
        if (token != "NX" || !matches) await view.SetAsync("condition", value);
        if (token == "XX" && !matches) await view.DeleteAsync("condition");
        var condition = token switch
        {
            "IFEQ" => RespireCondition.ValueEquals("condition", matches ? value : new byte[] { 1 }),
            "IFNE" => RespireCondition.ValueNotEquals("condition", matches ? new byte[] { 1 } : value),
            "NX" => RespireCondition.KeyNotExists("condition"),
            _ => RespireCondition.KeyExists("condition"),
        };
        await using var transaction = view.CreateTransaction();
        var first = transaction.Set("first", "one");
        var second = transaction.Set("second", "two");
        (await transaction.CommitAsync(condition)).Should().Be(matches);
        first.Status.Should().Be(matches ? RespirePendingStatus.Succeeded : RespirePendingStatus.Aborted);
        second.Status.Should().Be(first.Status);
        (await view.GetStringAsync("first")).Should().Be(matches ? "one" : null);
        (await view.GetStringAsync("second")).Should().Be(matches ? "two" : null);
        // A conditional abort must leave the shared connection ready for normal commands.
        (await view.PingAsync()).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Test]
    [MatrixDataSource]
    public async Task UnsupportedServersFailBeforeSendingWrites([Matrix(2, 3)] int protocol,
        [Matrix("redis:8.6-alpine", "valkey/valkey:9.1-alpine")] string image)
    {
        var server = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("output", "changed");
        Func<Task> commit = async () => await transaction.CommitAsync(RespireCondition.KeyNotExists("key"));
        await commit.Should().ThrowAsync<NotSupportedException>().WithMessage("*Valkey 9.2+*");
        pending.Error.Should().BeOfType<NotSupportedException>();
        (await client.GetStringAsync("output")).Should().BeNull();
        (await client.PingAsync()).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Test]
    [MatrixDataSource]
    public async Task MultipleConditionsAllMustMatchAndReadPreTransactionState([Matrix(2, 3)] int protocol)
    {
        var server = await servers.LeaseAsync(Valkey);
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        await client.SetAsync("version", "old");
        await using var first = client.CreateTransaction();
        _ = first.Set("version", "new");
        (await first.CommitAsync([RespireCondition.ValueEquals("version", "old"), RespireCondition.KeyNotExists("absent")]))
            .Should().BeTrue();
        await using var second = client.CreateTransaction();
        _ = second.Set("output", "bad");
        (await second.CommitAsync([RespireCondition.KeyExists("version"), RespireCondition.KeyExists("absent")]))
            .Should().BeFalse();
        (await client.GetStringAsync("version")).Should().Be("new");
        (await client.GetStringAsync("output")).Should().BeNull();
    }

    [Test]
    [MatrixDataSource]
    public async Task MissingKeysAndWrongTypesKeepServerSemantics([Matrix(2, 3)] int protocol)
    {
        var server = await servers.LeaseAsync(Valkey);
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        await using var missingEqual = client.CreateTransaction();
        (await missingEqual.CommitAsync(RespireCondition.ValueEquals("missing", "value"))).Should().BeFalse();
        await using var missingNotEqual = client.CreateTransaction();
        (await missingNotEqual.CommitAsync(RespireCondition.ValueNotEquals("missing", "value"))).Should().BeTrue();
        await client.Hashes.SetAsync("hash", "field", "value");
        await using var wrongType = client.CreateTransaction();
        var pending = wrongType.Set("output", "bad");
        Func<Task> commit = async () => await wrongType.CommitAsync(RespireCondition.ValueEquals("hash", "value"));
        await commit.Should().ThrowAsync<RespireServerException>();
        pending.Status.Should().Be(RespirePendingStatus.Faulted);
        (await client.GetStringAsync("output")).Should().BeNull();
        (await client.PingAsync()).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Test]
    [MatrixDataSource]
    public async Task ClusterConditionsRouteAndRejectCrossSlotKeys([Matrix(2, 3)] int protocol)
    {
        var cluster = await servers.SingleNodeClusterAsync(Valkey);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(cluster.Hostname, cluster.GetMappedPublicPort(6379))],
            UseCluster = true, Protocol = (RespireProtocol)protocol, Connections = 1,
        });
        var view = client.WithKeyPrefix($"{Guid.NewGuid():N}:");
        await view.SetAsync("{a}:version", "old");
        await using var transaction = view.CreateTransaction();
        var pending = transaction.Set("{a}:output", "committed");
        Func<Task> crossSlot = async () => await transaction.CommitAsync(RespireCondition.KeyExists("{b}:version"));
        await crossSlot.Should().ThrowAsync<InvalidOperationException>().WithMessage("*same hash slot*");
        pending.Status.Should().Be(RespirePendingStatus.Pending);
        (await transaction.CommitAsync(RespireCondition.ValueEquals("{a}:version", "old"))).Should().BeTrue();
        pending.Result.Should().BeTrue();
        await using var empty = view.CreateTransaction();
        (await empty.CommitAsync(RespireCondition.KeyExists("{a}:version"))).Should().BeTrue();
        await using var aborted = view.CreateTransaction();
        _ = aborted.Set("{a}:output", "bad");
        (await aborted.CommitAsync(RespireCondition.KeyNotExists("{a}:version"))).Should().BeFalse();
        (await view.GetStringAsync("{a}:output")).Should().Be("committed");
    }
}
