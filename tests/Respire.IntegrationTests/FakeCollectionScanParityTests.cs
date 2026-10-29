using FluentAssertions;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeCollectionScanParityTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    [SkipOnGarnet("Garnet 2.1.8 diverges from the Redis error, atomicity and reply-shape semantics this parity test pins.")]
    public async Task BinaryCollectionScansPreserveReplyShapesAndTtl(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await RespireClient.ConnectAsync(
            (fake?.CreateOptions() ?? RespireOptions.Parse(fixture.IsolatedConnectionString))
            with { Protocol = (RespireProtocol)protocol, Connections = 1 });
        byte[] member = [0, 255, 128, 13, 10];
        foreach (var command in new[] { "SSCAN", "HSCAN", "ZSCAN" })
        {
            var key = $"{Guid.NewGuid():N}:{command}";
            var (add, args) = command switch
            {
                "HSCAN" => ("HSET", new RespireValue[] { key, member, member, "", "" }),
                "ZSCAN" => ("ZADD", new RespireValue[] { key, "1.5", member, "-inf", "" }),
                _ => ("SADD", new RespireValue[] { key, member, "" }),
            };
            using var added = await client.ExecuteAsync(add, args);
            using var expiry = await client.ExecuteAsync("PEXPIRE", key, 60000);
            // Duplicate options use the final value; COUNT counts entries, not flattened values.
            using var page = await client.ExecuteAsync(command, key, 0,
                "match", "absent:*", "MATCH", "*", "count", 1, "COUNT", long.MaxValue);
            page.Type.Should().Be(RespireDataType.Array);
            page.Count.Should().Be(2);
            page[0].Type.Should().Be(RespireDataType.BulkString);
            page[0].AsString().Should().Be("0");
            page[1].Type.Should().Be(RespireDataType.Array);
            var stride = command == "SSCAN" ? 1 : 2;
            page[1].Count.Should().Be(2 * stride);
            var found = new Dictionary<string, string>();
            for (var i = 0; i < page[1].Count; i += stride)
            {
                page[1][i].Type.Should().Be(RespireDataType.BulkString);
                if (stride == 2) page[1][i + 1].Type.Should().Be(RespireDataType.BulkString);
                var name = Convert.ToHexString(page[1][i].AsBytes());
                found[name] = command switch
                {
                    "HSCAN" => Convert.ToHexString(page[1][i + 1].AsBytes()),
                    "ZSCAN" => page[1][i + 1].AsString(),
                    _ => name,
                };
            }
            found.Keys.Should().BeEquivalentTo(Convert.ToHexString(member), "");
            found[Convert.ToHexString(member)].Should().Be(command == "ZSCAN" ? "1.5" : Convert.ToHexString(member));
            found[""].Should().Be(command == "ZSCAN" ? "-inf" : "");
            using var ttl = await client.ExecuteAsync("PTTL", key);
            ttl.AsInteger().Should().BeGreaterThan(0);
        }
    }
}
