using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class Valkey92IntegrationTests(VersionedServerFixture servers)
{
    public const string Image = "valkey/valkey:9.2.0-rc1-alpine";

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExistenceAndLegacyReplies(int protocol)
    {
        var lease = await servers.LeaseAsync(Image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        (await client.Sets.ContainsAsync("set", "member")).Should().BeFalse();
        (await client.Sets.ContainsIfExistsAsync("set", "member")).Should().BeNull();
        await client.Sets.AddAsync("set", "member");
        (await client.Sets.ContainsIfExistsAsync("set", "member")).Should().BeTrue();
        (await client.Sets.ContainsIfExistsAsync("set", "other")).Should().BeFalse();
        (await client.SortedSets.RangeAsync("sorted")).Should().BeEmpty();
        (await client.SortedSets.RangeIfExistsAsync("sorted")).Should().BeNull();
        (await client.SortedSets.RangeIfExistsWithScoresAsync("sorted")).Should().BeNull();
        await client.SortedSets.AddAsync("sorted", "member", 2.5);
        (await client.SortedSets.RangeIfExistsAsync("sorted", 3, 4)).Should().BeEmpty();
        (await client.SortedSets.RangeIfExistsWithScoresAsync("sorted")).Should().Equal(new RespireSortedSetEntry("member", 2.5));
        await client.Streams.CreateGroupAsync("events", "workers", createStream: true);
        var pending = await client.Streams.PendingSummaryAsync("events", "workers");
        pending.Count.Should().Be(0);
        pending.SmallestId.Should().BeNull();
        pending.GreatestId.Should().BeNull();
        pending.Consumers.Should().BeEmpty();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MoveReplaceAndAdministration(int protocol)
    {
        var source = await servers.LeaseAsync(Image);
        var destination = await servers.LeaseAsync(Image);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [source.Endpoint], Database = source.Database, Connections = 1, AllowAdmin = true,
            Protocol = protocol == 2 ? RespireProtocol.Resp2 : RespireProtocol.Resp3,
        });
        await using var other = await RespireClient.ConnectAsync(destination.ConnectionString(protocol));
        await client.SetAsync("move", "new");
        await other.SetAsync("move", "old");
        (await client.Keys.MoveAsync("move", destination.Database)).Should().BeFalse();
        (await client.Keys.MoveAsync("move", destination.Database, replace: true)).Should().BeTrue();
        (await other.GetStringAsync("move")).Should().Be("new");
        var config = await client.Server.ConfigInfoAsync(["maxmemory", "appendonly"]);
        config.Should().Contain(info => info.Name == "maxmemory" && info.Type == "numeric" && info.Range.Count == 2);
        config.Should().Contain(info => info.Name == "appendonly" && info.Type == "bool");
        (await client.Server.AclDigestAsync()).Should().NotBeEmpty();
        var role = $"respire-{Guid.NewGuid():N}";
        (await client.Server.AclGetRoleAsync(role)).Should().BeNull();
        try
        {
            await client.Server.AclSetRoleAsync(role, ["+get", "~*", "&*", "alldbs"]);
            (await client.Server.AclRolesAsync()).Should().Contain(name => System.Text.Encoding.UTF8.GetString(name) == role);
            var definition = (await client.Server.AclGetRoleAsync(role))!;
            definition.Root.Commands.Should().Contain("+get");
            definition.Root.AdditionalFields["databases"].AsString().Should().Be("alldbs");
        }
        finally { await client.Server.AclDeleteRolesAsync([role]); }
        (await client.Server.AclGetRoleAsync(role)).Should().BeNull();
    }
}
