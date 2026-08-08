using FluentAssertions;
using StackExchange.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.PerClass)]
[NotInParallel("redis-integration")]
public class PrimitiveValueInteroperabilityTests(RedisTestFixture fixture)
{
    private RespireClient _respireClient = null!;
    private IConnectionMultiplexer _stackExchangeMultiplexer = null!;
    private IDatabase _stackExchangeDb = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _respireClient = await RespireClient.ConnectAsync($"{fixture.Host}:{fixture.Port}");
        _stackExchangeMultiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        _stackExchangeDb = _stackExchangeMultiplexer.GetDatabase();
        await _stackExchangeDb.ExecuteAsync("FLUSHDB");
    }

    [After(Test)]
    public async Task DisposeAsync()
    {
        await _respireClient.DisposeAsync();
        await _stackExchangeMultiplexer.DisposeAsync();
    }

    [Test]
    public async Task RespireTypedWrites_AreReadableByBothClients()
    {
        await AssertRespireWriteAsync("bool-true", true, "true");
        await AssertRespireWriteAsync("bool-false", false, "false");
        await AssertRespireWriteAsync("byte", byte.MaxValue, "255");
        await AssertRespireWriteAsync("sbyte", sbyte.MinValue, "-128");
        await AssertRespireWriteAsync("short", short.MinValue, "-32768");
        await AssertRespireWriteAsync("ushort", ushort.MaxValue, "65535");
        await AssertRespireWriteAsync("int", int.MinValue, "-2147483648");
        await AssertRespireWriteAsync("uint", uint.MaxValue, "4294967295");
        await AssertRespireWriteAsync("long", long.MinValue, "-9223372036854775808");
        await AssertRespireWriteAsync("ulong", ulong.MaxValue, "18446744073709551615");
        await AssertRespireWriteAsync("float", 3.5F, "3.5");
        await AssertRespireWriteAsync("double", -3.5D, "-3.5");
        await AssertRespireWriteAsync("decimal", 1234567890.123456789M, "1234567890.123456789");
    }

    [Test]
    public async Task StackExchangeRedisWrites_AreReadableByRespireTypedApis()
    {
        await AssertStackExchangeWriteAsync("bool-true", true, "1", true);
        await AssertStackExchangeWriteAsync("bool-false", false, "0", false);
        await AssertStackExchangeWriteAsync("byte", "255", "255", byte.MaxValue);
        await AssertStackExchangeWriteAsync("sbyte", "-128", "-128", sbyte.MinValue);
        await AssertStackExchangeWriteAsync("short", "-32768", "-32768", short.MinValue);
        await AssertStackExchangeWriteAsync("ushort", "65535", "65535", ushort.MaxValue);
        await AssertStackExchangeWriteAsync("int", int.MinValue, "-2147483648", int.MinValue);
        await AssertStackExchangeWriteAsync("uint", uint.MaxValue, "4294967295", uint.MaxValue);
        await AssertStackExchangeWriteAsync("long", long.MinValue, "-9223372036854775808", long.MinValue);
        await AssertStackExchangeWriteAsync("ulong", ulong.MaxValue, "18446744073709551615", ulong.MaxValue);
        await AssertStackExchangeWriteAsync("float", "3.5", "3.5", 3.5F);
        await AssertStackExchangeWriteAsync("double", -3.5D, "-3.5", -3.5D);
        await AssertStackExchangeWriteAsync("decimal", "1234567890.123456789", "1234567890.123456789", 1234567890.123456789M);
    }

    [Test]
    public async Task BinaryWrites_AreByteExactAcrossClients()
    {
        var stackExchangePayload = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var respirePayload = stackExchangePayload.Reverse().ToArray();

        await _stackExchangeDb.StringSetAsync("interop:binary:stackexchange", stackExchangePayload);

        var readByRespire = await _respireClient.GetBytesAsync("interop:binary:stackexchange");
        readByRespire.Should().Equal(stackExchangePayload);

        await _respireClient.SetAsync("interop:binary:respire", respirePayload);

        byte[]? readByStackExchange = await _stackExchangeDb.StringGetAsync("interop:binary:respire");
        readByStackExchange.Should().Equal(respirePayload);
    }

    private async Task AssertRespireWriteAsync<T>(string name, T value, string expectedRedisValue)
    {
        var key = $"interop:respire:{name}";

        (await _respireClient.SetAsync(key, value)).Should().BeTrue();

        var stackExchangeValue = await _stackExchangeDb.StringGetAsync(key);
        stackExchangeValue.IsNull.Should().BeFalse();
        stackExchangeValue.ToString().Should().Be(expectedRedisValue);
        (await _respireClient.GetAsync<T>(key)).Should().Be(value);
    }

    private async Task AssertStackExchangeWriteAsync<T>(
        string name,
        RedisValue value,
        string expectedRedisValue,
        T expectedRespireValue)
    {
        var key = $"interop:stackexchange:{name}";

        (await _stackExchangeDb.StringSetAsync(key, value)).Should().BeTrue();

        var stackExchangeValue = await _stackExchangeDb.StringGetAsync(key);
        stackExchangeValue.IsNull.Should().BeFalse();
        stackExchangeValue.ToString().Should().Be(expectedRedisValue);
        (await _respireClient.GetAsync<T>(key)).Should().Be(expectedRespireValue);
    }
}
