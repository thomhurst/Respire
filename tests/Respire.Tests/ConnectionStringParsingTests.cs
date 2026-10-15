using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ConnectionStringParsingTests
{
    [Test]
    [Arguments(-1)]
    [Arguments(1)]
    [Arguments(2000)]
    public async Task GatheredWriteTimeoutUriOverridesReceiveBudget(int milliseconds)
    {
        var options = RespireOptions.Parse($"redis://localhost?connectionIdleReadTimeoutMs=1000&gatheredWriteStallTimeoutMs={milliseconds}");
        await Assert.That(options.ConnectionIdleReadTimeout).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(options.GatheredWriteStallTimeout).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
    }

    [Test]
    [Arguments(-2)]
    [Arguments(int.MinValue)]
    [Arguments(0)]
    public async Task GatheredWriteTimeoutUriRejectsInvalidBounds(int milliseconds)
    {
        var error = await Assert.That(() => RespireOptions.Parse($"redis://localhost?gatheredWriteStallTimeoutMs={milliseconds}"))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains(nameof(RespireOptions.GatheredWriteStallTimeout));
    }

    [Test]
    [Arguments("")]
    [Arguments("4")]
    [Arguments("resp4")]
    [Arguments("oops")]
    [Arguments("2147483648")]
    public async Task UnsupportedProtocolNamesOptionAndPublicParameter(string value)
    {
        foreach (var prefix in new[] { "redis://localhost?", "localhost," })
        {
            var error = Assert.Throws<RespireConfigurationException>(() => RespireOptions.Parse($"{prefix}protocol={value}"));
            await Assert.That(((ArgumentException)error.InnerException!).ParamName).IsEqualTo("connectionString");
            await Assert.That(error.Message).Contains("protocol");
        }
    }

    [Test]
    [Arguments("2", RespireProtocol.Resp2)]
    [Arguments("resp2", RespireProtocol.Resp2)]
    [Arguments("RESP2", RespireProtocol.Resp2)]
    [Arguments("3", RespireProtocol.Resp3)]
    [Arguments("resp3", RespireProtocol.Resp3)]
    [Arguments("ReSp3", RespireProtocol.Resp3)]
    public async Task ProtocolAliasesHaveTheSameMeaningInBothSyntaxes(string value, RespireProtocol expected)
    {
        foreach (var prefix in new[] { "redis://localhost?", "localhost," })
        {
            await Assert.That(RespireOptions.Parse($"{prefix}protocol={value}").Protocol).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments("redis://localhost?", "connections")]
    [Arguments("redis://localhost?", "connectTimeoutMs")]
    [Arguments("redis://localhost?", "commandTimeoutMs")]
    [Arguments("redis://localhost?", "connectionIdleReadTimeoutMs")]
    [Arguments("redis://localhost?", "responseTimeoutMs")]
    [Arguments("redis://localhost?", "db")]
    [Arguments("localhost,", "defaultDatabase")]
    [Arguments("localhost,", "db")]
    [Arguments("localhost,", "connectTimeout")]
    [Arguments("localhost,", "asyncTimeout")]
    [Arguments("localhost,", "syncTimeout")]
    public async Task MalformedAndOverflowingNumbersNameOptionAndPublicParameter(string prefix, string option)
    {
        foreach (var value in new[] { "", "oops", "1.5", "2147483648", "-2147483649" })
        {
            var error = Assert.Throws<RespireConfigurationException>(() => RespireOptions.Parse($"{prefix}{option}={value}"));
            await Assert.That(((ArgumentException)error.InnerException!).ParamName).IsEqualTo("connectionString");
            await Assert.That(error.Message).Contains(option);
        }
    }

    [Test]
    [Arguments("oops")]
    [Arguments("2147483648")]
    public async Task InvalidDatabasePathNamesDatabaseAndPublicParameter(string value)
    {
        var error = Assert.Throws<RespireConfigurationException>(() => RespireOptions.Parse($"redis://localhost/{value}"));
        await Assert.That(((ArgumentException)error.InnerException!).ParamName).IsEqualTo("connectionString");
        await Assert.That(error.Message).Contains("database");
    }

    [Test]
    public async Task NumericParsingPreservesBoundariesWhitespaceAndTimeoutPrecedence()
    {
        var uri = RespireOptions.Parse("redis://localhost/0?connections=%20%2B1%20&db=2147483647" +
            "&connectTimeoutMs=2147483647&commandTimeoutMs=1&responseTimeoutMs=1");
        var comma = RespireOptions.Parse("localhost,defaultDatabase=2147483647,connectTimeout=2147483647," +
            "syncTimeout=2147483647,asyncTimeout= +1 ");
        await Assert.That(uri.Connections).IsEqualTo(1);
        await Assert.That(uri.Database).IsEqualTo(int.MaxValue);
        await Assert.That(comma.Database).IsEqualTo(int.MaxValue);
        await Assert.That(uri.ConnectTimeout).IsEqualTo(TimeSpan.FromMilliseconds(int.MaxValue));
        await Assert.That(comma.ConnectTimeout).IsEqualTo(uri.ConnectTimeout);
        await Assert.That(uri.CommandTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1));
        await Assert.That(comma.CommandTimeout).IsEqualTo(uri.CommandTimeout);
        await Assert.That(uri.ConnectionIdleReadTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1));
    }

    [Test]
    [Arguments("redis://localhost?connectTimeout=1")]
    public async Task SyntaxSpecificOptionsRemainUnsupported(string connectionString)
    {
        await Assert.That(() => RespireOptions.Parse(connectionString)).Throws<RespireConfigurationException>();
    }
}
