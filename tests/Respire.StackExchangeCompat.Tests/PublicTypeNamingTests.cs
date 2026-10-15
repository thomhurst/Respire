using Respire;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

// A consumer namespace is essential: nesting under Respire would hide ambiguities.
namespace MigrationConsumer.Tests;

public class PublicTypeNamingTests
{
    [Test]
    public void PublicTopLevelTypeNamesDoNotCollide()
    {
        var nativeNames = typeof(RespireClient).Assembly.GetExportedTypes()
            .Where(static type => !type.IsNested)
            .Select(static type => type.Name.Split('`')[0]);
        var redisNames = typeof(ConnectionMultiplexer).Assembly.GetExportedTypes()
            .Where(static type => !type.IsNested)
            .Select(static type => type.Name.Split('`')[0]);

        Assert.Empty(nativeNames.Intersect(redisNames, StringComparer.Ordinal).Order());
    }

    [Test]
    public void BothNamespacesSupportUnqualifiedTypeNames()
    {
        Type[] nativeTypes =
        [
            typeof(RespireBitFieldEncoding), typeof(RespireBitFieldOffset),
            typeof(RespireBitFieldOperation), typeof(RespireBitFieldOverflow),
            typeof(RespireExpireWhen), typeof(RespireGeoEntry), typeof(RespireGeoPosition),
            typeof(RespireGeoSearchShape), typeof(RespireGeoUnit), typeof(RespireListMoveOrder),
            typeof(RespireListSide), typeof(RespireSortedSetEntry), typeof(RespireSortedSetEntry<int>),
            typeof(RespireStreamAddOptions), typeof(RespireStreamNackMode),
        ];
        Type[] redisTypes =
        [
            typeof(BitFieldEncoding), typeof(BitFieldOffset), typeof(BitFieldOperation),
            typeof(BitFieldOverflow), typeof(ExpireWhen), typeof(GeoEntry), typeof(GeoPosition),
            typeof(GeoSearchShape), typeof(GeoUnit), typeof(ListMoveOrder), typeof(ListSide),
            typeof(SortedSetEntry), typeof(StreamAddOptions), typeof(StreamNackMode),
        ];

        Assert.All(nativeTypes, static type => Assert.Equal("Respire", type.Namespace));
        Assert.All(redisTypes, static type => Assert.Equal("StackExchange.Redis", type.Namespace));
    }

    // Compile the migration guide's example against both public APIs without a Redis server.
    private static async Task RefreshExpiryAsync(IRespireClient client, IDatabase database)
    {
        await database.KeyExpireAsync("legacy:key", TimeSpan.FromMinutes(5), ExpireWhen.Always);
        await client.Keys.ExpireAsync("native:key", TimeSpan.FromMinutes(5), RespireExpireWhen.Always);
    }
}
