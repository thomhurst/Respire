using FluentAssertions;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeKeyspaceParityTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task TimeReplyAndKeyEnumerationMatch(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await RespireClient.ConnectAsync(
            (fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString)) with { Protocol = (RespProtocol)protocol });
        using var time = await client.ExecuteAsync("TIME");
        time.Type.Should().Be(RespDataType.Array);
        time.Count.Should().Be(2);
        time[0].Type.Should().Be(RespDataType.BulkString);
        time[1].Type.Should().Be(RespDataType.BulkString);
        long.Parse(time[1].AsString()).Should().BeInRange(0, 999999);
        (await client.Server.TimeAsync()).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

        var prefix = Guid.NewGuid().ToString("N") + ":";
        var expected = Enumerable.Range(0, 30).Select(i => prefix + i).ToArray();
        foreach (var key in expected) await client.SetAsync(key, "value");
        using var keys = await client.ExecuteAsync("KEYS", prefix + "*");
        keys.Select(value => value.AsString()).Should().BeEquivalentTo(expected);
        var found = new HashSet<string>();
        await foreach (var key in client.Keys.ScanAsync(prefix + "*", countHint: 1))
        {
            found.Add(key);
            await client.DeleteAsync(key);
        }
        found.Should().BeEquivalentTo(expected);
        using var remaining = await client.ExecuteAsync("KEYS", prefix + "*");
        remaining.Count.Should().Be(0);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task GlobMatchingPreservesBinaryKeys(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await RespireClient.ConnectAsync(
            (fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString)) with { Protocol = (RespProtocol)protocol });
        var prefix = System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N") + ":");
        byte[] key = [.. prefix, 0, 255, (byte)'c', (byte)'*'];
        using var set = await client.ExecuteAsync("SET", key, "value");
        foreach (byte[] suffix in new byte[][]
        {
            [0, 255, (byte)'?', (byte)'\\', (byte)'*'],
            [0, 255, .. "[a-d]\\*"u8.ToArray()],
            [0, 255, .. "[^x-z]*"u8.ToArray()],
        })
        {
            byte[] pattern = [.. prefix, .. suffix];
            using var keys = await client.ExecuteAsync("KEYS", pattern);
            keys.Count.Should().Be(1);
            keys[0].AsBytes().Should().Equal(key);
            var cursor = "0";
            var found = new List<string>();
            do
            {
                using var page = await client.ExecuteAsync("SCAN", cursor, "MATCH", pattern, "COUNT", 10);
                cursor = page[0].AsString();
                found.AddRange(page[1].Select(value => Convert.ToHexString(value.AsBytes())));
            } while (cursor != "0");
            found.Distinct().Should().Equal(Convert.ToHexString(key));
        }
        using var deleted = await client.ExecuteAsync("DEL", key);
    }
}
