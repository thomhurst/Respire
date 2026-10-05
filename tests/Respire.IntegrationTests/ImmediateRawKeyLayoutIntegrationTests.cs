using FluentAssertions;
using Respire.Commands;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ImmediateRawKeyLayoutIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ImmediateLayoutsMatchServerKeyDiscovery(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        (string Name, RespireValue[] Args)[] cases =
        [
            ("BLPOP", ["first", "second", 1]),
            ("BRPOP", ["first", "second", 1]),
            ("BZPOPMIN", ["first", "second", 1]),
            ("BZPOPMAX", ["first", "second", 1]),
            ("BLMOVE", ["first", "second", "LEFT", "RIGHT", 1]),
            ("BRPOPLPUSH", ["first", "second", 1]),
            ("BLMPOP", [1, 2, "first", "second", "LEFT"]),
            ("BZMPOP", [1, 2, "first", "second", "MIN"]),
            ("MSETEX", [2, "first", "value", "second", "value", "PX", 100]),
            ("XREAD", ["COUNT", 2, "BLOCK", 1, "STREAMS", "first", "second", "0", "0"]),
            ("XREADGROUP", ["GROUP", "STREAMS", "consumer", "NOACK", "STREAMS", "first", "second", ">", ">"]),
            ("MIGRATE", ["host", 6379, "", 0, 100, "AUTH2", "user", "KEYS", "KEYS", "first", "second"]),
            ("MIGRATE", ["host", 6379, "first", 0, 100]),
            ("TS.ADD", ["first", 1, 1.5, "LABELS", "room", "1"]),
            ("TS.MADD", ["first", 1, 1.5, "second", 2, 2.5]),
            ("TS.CREATERULE", ["first", "second", "AGGREGATION", "avg", 10]),
            ("TS.DELETERULE", ["first", "second"]),
            ("TS.RANGE", ["first", "-", "+", "COUNT", 1]),
        ];
        foreach (var (name, args) in cases)
        {
            RawCommandKeyLayouts.TryGetLayout(name, args, out var layout).Should().BeTrue();
            using var keys = await client.ExecuteAsync(RespireCommands.Server.COMMAND_GETKEYS, [name, .. args]);
            var expected = Enumerable.Range(0, layout.Count)
                .Select(index => args[layout.Start + index * layout.Stride].ToString()).ToArray();
            Enumerable.Range(0, keys.Count).Select(index => keys[index].AsString()).Should().Equal(expected, name);
        }
    }
}
