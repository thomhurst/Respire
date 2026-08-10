using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using StackExchange.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class PrimitiveValueInteroperabilityTests(RedisTestContainer fixture)
{
    private RespireClient _respireClient = null!;
    private IConnectionMultiplexer _stackExchangeMultiplexer = null!;
    private IDatabase _stackExchangeDb = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _respireClient = await RespireClient.ConnectAsync(fixture.ConnectionString);
        _stackExchangeMultiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
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
        await AssertRespireWriteAsync("bool-true", true, "1");
        await AssertRespireWriteAsync("bool-false", false, "0");
        await AssertRespireWriteAsync("byte-zero", byte.MinValue, "0");
        await AssertRespireWriteAsync("byte", byte.MaxValue, "255");
        await AssertRespireWriteAsync("sbyte-min", sbyte.MinValue, "-128");
        await AssertRespireWriteAsync("sbyte-max", sbyte.MaxValue, "127");
        await AssertRespireWriteAsync("short-min", short.MinValue, "-32768");
        await AssertRespireWriteAsync("short-max", short.MaxValue, "32767");
        await AssertRespireWriteAsync("ushort-zero", ushort.MinValue, "0");
        await AssertRespireWriteAsync("ushort", ushort.MaxValue, "65535");
        await AssertRespireWriteAsync("int-min", int.MinValue, "-2147483648");
        await AssertRespireWriteAsync("int-zero", 0, "0");
        await AssertRespireWriteAsync("int-max", int.MaxValue, "2147483647");
        await AssertRespireWriteAsync("uint-zero", uint.MinValue, "0");
        await AssertRespireWriteAsync("uint", uint.MaxValue, "4294967295");
        await AssertRespireWriteAsync("long-min", long.MinValue, "-9223372036854775808");
        await AssertRespireWriteAsync("long-zero", 0L, "0");
        await AssertRespireWriteAsync("long-max", long.MaxValue, "9223372036854775807");
        await AssertRespireWriteAsync("ulong-zero", ulong.MinValue, "0");
        await AssertRespireWriteAsync("ulong", ulong.MaxValue, "18446744073709551615");
        await AssertRespireWriteAsync("float-negative", -123.5F, "-123.5");
        await AssertRespireWriteAsync("float-zero", 0F, "0");
        await AssertRespireWriteAsync("float-positive", 123.5F, "123.5");
        await AssertRespireWriteAsync("double-negative", -123.5D, "-123.5");
        await AssertRespireWriteAsync("double-zero", 0D, "0");
        await AssertRespireWriteAsync("double-positive", 123.5D, "123.5");
        await AssertRespireWriteAsync("decimal-min", decimal.MinValue, "-79228162514264337593543950335");
        await AssertRespireWriteAsync("decimal-zero", decimal.Zero, "0");
        await AssertRespireWriteAsync("decimal-fraction", 1234567890.123456789M, "1234567890.123456789");
        await AssertRespireWriteAsync("decimal-max", decimal.MaxValue, "79228162514264337593543950335");
    }

    [Test]
    public async Task StackExchangeRedisWrites_AreReadableByRespireTypedApis()
    {
        await AssertStackExchangeWriteAsync("bool-true", true, "1", true);
        await AssertStackExchangeWriteAsync("bool-false", false, "0", false);
        await AssertStackExchangeWriteAsync("byte-min", "0", "0", byte.MinValue);
        await AssertStackExchangeWriteAsync("byte-max", "255", "255", byte.MaxValue);
        await AssertStackExchangeWriteAsync("sbyte-min", "-128", "-128", sbyte.MinValue);
        await AssertStackExchangeWriteAsync("sbyte-max", "127", "127", sbyte.MaxValue);
        await AssertStackExchangeWriteAsync("short-min", "-32768", "-32768", short.MinValue);
        await AssertStackExchangeWriteAsync("short-max", "32767", "32767", short.MaxValue);
        await AssertStackExchangeWriteAsync("ushort-min", "0", "0", ushort.MinValue);
        await AssertStackExchangeWriteAsync("ushort-max", "65535", "65535", ushort.MaxValue);
        await AssertStackExchangeWriteAsync("int-min", int.MinValue, "-2147483648", int.MinValue);
        await AssertStackExchangeWriteAsync("int-max", int.MaxValue, "2147483647", int.MaxValue);
        await AssertStackExchangeWriteAsync("uint-min", uint.MinValue, "0", uint.MinValue);
        await AssertStackExchangeWriteAsync("uint-max", uint.MaxValue, "4294967295", uint.MaxValue);
        await AssertStackExchangeWriteAsync("long-min", long.MinValue, "-9223372036854775808", long.MinValue);
        await AssertStackExchangeWriteAsync("long-max", long.MaxValue, "9223372036854775807", long.MaxValue);
        await AssertStackExchangeWriteAsync("ulong-min", ulong.MinValue, "0", ulong.MinValue);
        await AssertStackExchangeWriteAsync("ulong-max", ulong.MaxValue, "18446744073709551615", ulong.MaxValue);
        await AssertStackExchangeWriteAsync("float-negative", "-123.5", "-123.5", -123.5F);
        await AssertStackExchangeWriteAsync("float-exponent", "1.25e-20", "1.25e-20", 1.25e-20F);
        await AssertStackExchangeWriteAsync("double-negative", -123.5D, "-123.5", -123.5D);
        await AssertStackExchangeWriteAsync("double-exponent", "1.25e-200", "1.25e-200", 1.25e-200D);
        await AssertStackExchangeWriteAsync(
            "decimal-min", "-79228162514264337593543950335", "-79228162514264337593543950335", decimal.MinValue);
        await AssertStackExchangeWriteAsync(
            "decimal-fraction", "1234567890.123456789", "1234567890.123456789", 1234567890.123456789M);
        await AssertStackExchangeWriteAsync(
            "decimal-max", "79228162514264337593543950335", "79228162514264337593543950335", decimal.MaxValue);
    }

    [Test]
    public async Task BooleanEncodings_FromStackExchangeRedis_AreAccepted()
    {
        await AssertStackExchangeWriteAsync("bool-word-true", "true", "true", true);
        await AssertStackExchangeWriteAsync("bool-word-false", "false", "false", false);
        await AssertStackExchangeWriteAsync("bool-one", "1", "1", true);
        await AssertStackExchangeWriteAsync("bool-zero", "0", "0", false);
        await AssertStackExchangeWriteAsync("bool-whitespace-true", "\t true \r\n", "\t true \r\n", true);
        await AssertStackExchangeWriteAsync("bool-whitespace-false", "\r\nfalse\t", "\r\nfalse\t", false);
    }

    [Test]
    public async Task NullableNullEncodings_FromStackExchangeRedis_AreAccepted()
    {
        await AssertStackExchangeWriteAsync<int?>("nullable-int", " null ", " null ", null);
        await AssertStackExchangeWriteAsync<bool?>("nullable-bool", "\tnull\r\n", "\tnull\r\n", null);
    }

    [Test]
    public async Task InvalidPrimitiveEncodings_FromStackExchangeRedis_AreRejected()
    {
        await AssertRespireReadThrowsAsync<byte>("byte-overflow", "256");
        await AssertRespireReadThrowsAsync<byte>("byte-negative", "-1");
        await AssertRespireReadThrowsAsync<sbyte>("sbyte-underflow", "-129");
        await AssertRespireReadThrowsAsync<short>("short-overflow", "32768");
        await AssertRespireReadThrowsAsync<int>("int-fraction", "1.5");
        await AssertRespireReadThrowsAsync<int>("int-overflow", "2147483648");
        await AssertRespireReadThrowsAsync<uint>("uint-negative", "-1");
        await AssertRespireReadThrowsAsync<ulong>("ulong-negative", "-1");
        await AssertRespireReadThrowsAsync<float>("float-nan", "NaN");
        await AssertRespireReadThrowsAsync<double>("double-infinity", "Infinity");
        await AssertRespireReadThrowsAsync<double>("double-overflow", "1e400");
        await AssertRespireReadThrowsAsync<decimal>("decimal-group-separator", "1,234");
    }

    [Test]
    public async Task PrimitiveFormatting_IsCultureInvariantAcrossClients()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            await AssertRespireWriteAsync("culture-double", 1234.5D, "1234.5");
            await AssertRespireWriteAsync("culture-decimal", 1234.5M, "1234.5");
            await AssertStackExchangeWriteAsync("culture-stack-double", 1234.5D, "1234.5", 1234.5D);
            await AssertStackExchangeWriteAsync("culture-stack-decimal", "1234.5", "1234.5", 1234.5M);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public async Task FloatingPointBoundaries_KeepExactValuesAcrossClients()
    {
        await AssertRespireWriteAsync(
            "float-min", float.MinValue, float.MinValue.ToString("R", CultureInfo.InvariantCulture));
        await AssertRespireWriteAsync(
            "float-max", float.MaxValue, float.MaxValue.ToString("R", CultureInfo.InvariantCulture));
        await AssertRespireWriteAsync(
            "float-epsilon", float.Epsilon, float.Epsilon.ToString("R", CultureInfo.InvariantCulture));
        await AssertRespireWriteAsync(
            "double-min", double.MinValue, double.MinValue.ToString("R", CultureInfo.InvariantCulture));
        await AssertRespireWriteAsync(
            "double-max", double.MaxValue, double.MaxValue.ToString("R", CultureInfo.InvariantCulture));
        await AssertRespireWriteAsync(
            "double-epsilon", double.Epsilon, double.Epsilon.ToString("R", CultureInfo.InvariantCulture));

        await AssertStackExchangeWriteAsync(
            "stack-float-min", float.MinValue.ToString("R", CultureInfo.InvariantCulture),
            float.MinValue.ToString("R", CultureInfo.InvariantCulture), float.MinValue);
        await AssertStackExchangeWriteAsync(
            "stack-float-epsilon", float.Epsilon.ToString("R", CultureInfo.InvariantCulture),
            float.Epsilon.ToString("R", CultureInfo.InvariantCulture), float.Epsilon);
        await AssertStackExchangeWriteAsync(
            "stack-double-max", double.MaxValue,
            double.MaxValue.ToString("R", CultureInfo.InvariantCulture), double.MaxValue);
        await AssertStackExchangeWriteAsync(
            "stack-double-epsilon", double.Epsilon,
            "4.9406564584124654E-324", double.Epsilon);

        await AssertSignedZeroAsync("float-negative-zero", -0F);
        await AssertSignedZeroAsync("double-negative-zero", -0D);
    }

    [Test]
    public async Task TextWrites_AreCharacterExactAcrossClients()
    {
        var values = new (string Name, string Value)[]
        {
            ("empty", string.Empty),
            ("single", "x"),
            ("whitespace", " \t\r\n "),
            ("embedded-null", "before\0after"),
            ("control-characters", "\u0001\u0002\u001e\u001f"),
            ("quotes-and-slashes", "'\"`\\/{}[](),:;"),
            ("unicode", "Zażółć gęślą jaźń — 東京 — مرحبا — 😀🚀"),
            ("composed-unicode", "é"),
            ("decomposed-unicode", "e\u0301"),
            ("large-ascii", new string('x', 65_537)),
            ("large-unicode", string.Concat(Enumerable.Repeat("水😀", 16_384))),
        };

        foreach (var (name, value) in values)
        {
            await AssertTextRoundTripAsync(name, value);
        }
    }

    [Test]
    public async Task BinaryWrites_AreByteExactAcrossClients()
    {
        var sizes = new[] { 0, 1, 15, 16, 255, 256, 1_023, 1_024, 65_535, 65_536, 65_537, 262_144 };

        foreach (var size in sizes)
        {
            var payload = Enumerable.Range(0, size)
                .Select(index => (byte)((index * 31 + 17) & 0xff))
                .ToArray();

            await AssertBinaryRoundTripAsync(size.ToString(CultureInfo.InvariantCulture), payload);
        }
    }

    [Test]
    public async Task JsonValues_AreInteroperableAcrossClients()
    {
        var respireValue = new InteropPayload(
            42,
            "Respire 東京 😀",
            ["alpha", "", "omega"],
            null,
            new Dictionary<string, decimal> { ["minimum"] = decimal.MinValue, ["fraction"] = 1.25M });
        var stackExchangeValue = new InteropPayload(
            -7,
            "StackExchange.Redis مرحبا",
            ["one", "two"],
            "present",
            new Dictionary<string, decimal> { ["maximum"] = decimal.MaxValue });

        await _respireClient.SetAsync("interop:json:respire", respireValue);

        var rawRespireValue = await _stackExchangeDb.StringGetAsync("interop:json:respire");
        rawRespireValue.ToString().Should().Be(JsonSerializer.Serialize(respireValue));

        await _stackExchangeDb.StringSetAsync("interop:json:stackexchange", JsonSerializer.Serialize(stackExchangeValue));

        var readByRespire = await _respireClient.GetAsync<InteropPayload>("interop:json:stackexchange");
        readByRespire.Should().BeEquivalentTo(stackExchangeValue);
    }

    [Test]
    public async Task MissingEmptyAndOverwrittenValues_AreDistinguishedAcrossClients()
    {
        const string missingKey = "interop:lifecycle:missing";
        const string sharedKey = "interop:lifecycle:shared";

        (await _respireClient.GetStringAsync(missingKey)).Should().BeNull();
        (await _stackExchangeDb.StringGetAsync(missingKey)).IsNull.Should().BeTrue();

        await _stackExchangeDb.StringSetAsync(sharedKey, string.Empty);
        (await _respireClient.GetStringAsync(sharedKey)).Should().BeEmpty();
        (await _stackExchangeDb.StringGetAsync(sharedKey)).IsNull.Should().BeFalse();

        await _respireClient.SetAsync(sharedKey, 42);
        (await _stackExchangeDb.StringGetAsync(sharedKey)).ToString().Should().Be("42");

        var binaryValue = new byte[] { 0, 255, 13, 10, 0, 128 };
        await _stackExchangeDb.StringSetAsync(sharedKey, binaryValue);
        (await _respireClient.GetBytesAsync(sharedKey)).Should().Equal(binaryValue);

        await _respireClient.SetAsync(sharedKey, "final-value");
        (await _stackExchangeDb.StringGetAsync(sharedKey)).ToString().Should().Be("final-value");
    }

    [Test]
    public async Task ExpiringWrites_PreserveValueAndTtlAcrossClients()
    {
        var expiry = TimeSpan.FromMinutes(5);

        await _respireClient.SetAsync("interop:ttl:respire", "from-respire", expiry);

        (await _stackExchangeDb.StringGetAsync("interop:ttl:respire")).ToString().Should().Be("from-respire");
        var stackExchangeTtl = await _stackExchangeDb.KeyTimeToLiveAsync("interop:ttl:respire");
        stackExchangeTtl.Should().NotBeNull();
        stackExchangeTtl.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
        stackExchangeTtl.Should().BeLessThanOrEqualTo(expiry);

        await _stackExchangeDb.StringSetAsync("interop:ttl:stackexchange", "from-stackexchange", expiry);

        (await _respireClient.GetStringAsync("interop:ttl:stackexchange")).Should().Be("from-stackexchange");
        var respireTtl = await _respireClient.Keys.ExpiryAsync("interop:ttl:stackexchange");
        respireTtl.Exists.Should().BeTrue();
        respireTtl.HasExpiry.Should().BeTrue();
        respireTtl.TimeToLive.Should().NotBeNull();
        respireTtl.TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
        respireTtl.TimeToLive.Should().BeLessThanOrEqualTo(expiry);
    }

    [Test]
    public async Task HashValues_AreInteroperableAcrossClients()
    {
        const string key = "interop:hash";
        var stackExchangeBytes = new byte[] { 0, 1, 127, 128, 254, 255 };
        // C# 14 first-class spans would bind bare .Reverse() to the in-place void
        // MemoryExtensions overload; keep the LINQ copy semantics explicit.
        var respireBytes = Enumerable.Reverse(stackExchangeBytes).ToArray();

        await _stackExchangeDb.HashSetAsync(key,
        [
            new HashEntry("text", "stack\0value"),
            new HashEntry("empty", string.Empty),
            new HashEntry("number", "2147483647"),
            new HashEntry("binary", stackExchangeBytes),
        ]);

        (await _respireClient.Hashes.GetStringAsync(key, "text")).Should().Be("stack\0value");
        (await _respireClient.Hashes.GetStringAsync(key, "empty")).Should().BeEmpty();
        (await _respireClient.Hashes.GetAsync<int>(key, "number")).Should().Be(int.MaxValue);
        (await _respireClient.Hashes.GetBytesAsync(key, "binary")).Should().Equal(stackExchangeBytes);
        (await _respireClient.Hashes.GetStringAsync(key, "missing")).Should().BeNull();

        await _respireClient.Hashes.SetAsync(key, "text", "respire-value");
        await _respireClient.Hashes.SetAsync(key, "binary", respireBytes);
        await _respireClient.Hashes.SetAsync(key, "integer", long.MinValue);
        await _respireClient.Hashes.SetAsync(key, "boolean", true);
        await _respireClient.Hashes.SetAsync(key, "boolean-raw", (RespireValue)true);

        (await _stackExchangeDb.HashGetAsync(key, "text")).ToString().Should().Be("respire-value");
        byte[]? binaryReadByStackExchange = await _stackExchangeDb.HashGetAsync(key, "binary");
        binaryReadByStackExchange.Should().Equal(respireBytes);
        (await _stackExchangeDb.HashGetAsync(key, "integer")).ToString().Should().Be("-9223372036854775808");
        // Generic and explicit RespireValue Boolean writes share the Redis-native "1"/"0"
        // encoding. Both read back as bool.
        (await _stackExchangeDb.HashGetAsync(key, "boolean")).ToString().Should().Be("1");
        (await _stackExchangeDb.HashGetAsync(key, "boolean-raw")).ToString().Should().Be("1");
        (await _respireClient.Hashes.GetAsync<bool>(key, "boolean")).Should().BeTrue();
        (await _respireClient.Hashes.GetAsync<bool>(key, "boolean-raw")).Should().BeTrue();
    }

    [Test]
    public async Task ListValues_AreInteroperableAcrossClients()
    {
        const string listKey = "interop:list";
        var initialList = new RedisValue[] { "stack-first", string.Empty, "東京", "stack-last" };

        await _stackExchangeDb.ListRightPushAsync(listKey, initialList);
        (await _respireClient.Lists.RangeAsync(listKey)).Should().Equal("stack-first", string.Empty, "東京", "stack-last");

        await _respireClient.Lists.LeftPushAsync(listKey, "respire-first");
        await _respireClient.Lists.RightPushAsync(listKey, "respire-last");

        var listReadByStackExchange = (await _stackExchangeDb.ListRangeAsync(listKey))
            .Select(value => value.ToString());
        listReadByStackExchange.Should().Equal(
            "respire-first", "stack-first", string.Empty, "東京", "stack-last", "respire-last");
    }

    [Test]
    public async Task SetValues_AreInteroperableAcrossClients()
    {
        const string setKey = "interop:set";
        await _stackExchangeDb.SetAddAsync(setKey, ["alpha", string.Empty, "東京"]);

        (await _respireClient.Sets.MembersAsync(setKey)).Should().BeEquivalentTo("alpha", string.Empty, "東京");
        await _respireClient.Sets.AddAsync(setKey, "omega", "東京");

        var setReadByStackExchange = (await _stackExchangeDb.SetMembersAsync(setKey))
            .Select(value => value.ToString());
        setReadByStackExchange.Should().BeEquivalentTo("alpha", string.Empty, "東京", "omega");
    }

    [Test]
    public async Task SortedSetValuesAndScores_AreInteroperableAcrossClients()
    {
        const string key = "interop:sorted-set";

        await _stackExchangeDb.SortedSetAddAsync(key,
        [
            new StackExchange.Redis.SortedSetEntry("negative", -10.5),
            new StackExchange.Redis.SortedSetEntry("zero", 0),
            new StackExchange.Redis.SortedSetEntry("positive", 1.25e100),
        ]);

        var readByRespire = await _respireClient.SortedSets.RangeWithScoresAsync(key);
        readByRespire.Should().Equal(
            new Respire.SortedSetEntry("negative", -10.5),
            new Respire.SortedSetEntry("zero", 0),
            new Respire.SortedSetEntry("positive", 1.25e100));

        await _respireClient.SortedSets.AddAsync(key, "respire", 3.75);

        (await _stackExchangeDb.SortedSetScoreAsync(key, "respire")).Should().Be(3.75);
        var readByStackExchange = await _stackExchangeDb.SortedSetRangeByRankWithScoresAsync(key);
        readByStackExchange.Select(entry => entry.Element.ToString()).Should()
            .Equal("negative", "zero", "respire", "positive");
        readByStackExchange.Select(entry => entry.Score).Should()
            .Equal(-10.5, 0, 3.75, 1.25e100);
    }

    private async Task AssertTextRoundTripAsync(string name, string value)
    {
        var respireKey = $"interop:text:respire:{name}";
        var stackExchangeKey = $"interop:text:stackexchange:{name}";

        (await _respireClient.SetAsync(respireKey, value)).Should().BeTrue("case {0}", name);
        string? readByStackExchange = await _stackExchangeDb.StringGetAsync(respireKey);
        readByStackExchange.Should().Be(value, "case {0}", name);

        (await _stackExchangeDb.StringSetAsync(stackExchangeKey, value)).Should().BeTrue("case {0}", name);
        (await _respireClient.GetStringAsync(stackExchangeKey)).Should().Be(value, "case {0}", name);
    }

    private async Task AssertBinaryRoundTripAsync(string name, byte[] value)
    {
        var respireKey = $"interop:binary:respire:{name}";
        var stackExchangeKey = $"interop:binary:stackexchange:{name}";

        (await _respireClient.SetAsync(respireKey, value)).Should().BeTrue("case {0}", name);
        byte[]? readByStackExchange = await _stackExchangeDb.StringGetAsync(respireKey);
        readByStackExchange.Should().Equal(value, "case {0}", name);

        (await _stackExchangeDb.StringSetAsync(stackExchangeKey, value)).Should().BeTrue("case {0}", name);
        (await _respireClient.GetBytesAsync(stackExchangeKey)).Should().Equal(value, "case {0}", name);
    }

    private async Task AssertRespireWriteAsync<T>(string name, T value, string expectedRedisValue)
    {
        var key = $"interop:respire:{name}";

        (await _respireClient.SetAsync(key, value)).Should().BeTrue("case {0}", name);

        var stackExchangeValue = await _stackExchangeDb.StringGetAsync(key);
        stackExchangeValue.IsNull.Should().BeFalse("case {0}", name);
        stackExchangeValue.ToString().Should().Be(expectedRedisValue, "case {0}", name);
        (await _respireClient.GetAsync<T>(key)).Should().Be(value, "case {0}", name);
    }

    private async Task AssertStackExchangeWriteAsync<T>(
        string name,
        RedisValue value,
        string expectedRedisValue,
        T expectedRespireValue)
    {
        var key = $"interop:stackexchange:{name}";

        (await _stackExchangeDb.StringSetAsync(key, value)).Should().BeTrue("case {0}", name);

        var stackExchangeValue = await _stackExchangeDb.StringGetAsync(key);
        stackExchangeValue.IsNull.Should().BeFalse("case {0}", name);
        stackExchangeValue.ToString().Should().Be(expectedRedisValue, "case {0}", name);
        (await _respireClient.GetAsync<T>(key)).Should().Be(expectedRespireValue, "case {0}", name);
    }

    private async Task AssertRespireReadThrowsAsync<T>(string name, string value)
    {
        var key = $"interop:invalid:{name}";
        await _stackExchangeDb.StringSetAsync(key, value);

        Func<Task> read = async () => await _respireClient.GetAsync<T>(key);
        await read.Should().ThrowAsync<FormatException>("case {0}", name);
    }

    private async Task AssertSignedZeroAsync<T>(string name, T value)
    {
        var respireKey = $"interop:signed-zero:respire:{name}";
        var stackExchangeKey = $"interop:signed-zero:stackexchange:{name}";

        await _respireClient.SetAsync(respireKey, value);
        (await _stackExchangeDb.StringGetAsync(respireKey)).ToString().Should().Be("-0", "case {0}", name);

        await _stackExchangeDb.StringSetAsync(stackExchangeKey, "-0");

        var result = await _respireClient.GetAsync<T>(stackExchangeKey);
        var bits = result switch
        {
            float single => BitConverter.SingleToInt32Bits(single),
            double @double => BitConverter.DoubleToInt64Bits(@double),
            _ => throw new InvalidOperationException($"Unsupported floating-point type {typeof(T)}."),
        };
        bits.Should().BeLessThan(0, "negative zero must retain its sign bit for case {0}", name);
    }

    private sealed record InteropPayload(
        int Id,
        string Name,
        string[] Tags,
        string? Optional,
        Dictionary<string, decimal> Measurements);
}
