using System.Net.Security;
using Amazon;
using Amazon.Runtime;
using Respire.Aws;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class CredentialFormattingTests
{
    [Test]
    public async Task ParsedOptionsAndCredentialsFormattingRedactsSecrets()
    {
        var options = RespireOptions.Parse("redis://private-user:private-password@localhost:6379");
        var credentials = new RespireCredentials("private-user", "private-token");

        await Assert.That(options.ToString()).IsEqualTo("RespireOptions { <redacted> }");
        await Assert.That(credentials.ToString()).IsEqualTo("RespireCredentials { <redacted> }");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("private-password-🔐\r\n")]
    public async Task OptionsFormattingRedactsCredentials(string? password)
    {
        var options = new RespireOptions
        {
            Username = "private-user",
            Password = password,
            SentinelUsername = "private-sentinel-user",
            SentinelPassword = password,
        };

        await Assert.That(options.ToString()).IsEqualTo("RespireOptions { <redacted> }");
        await Assert.That($"{options}").IsEqualTo("RespireOptions { <redacted> }");
        await Assert.That((options with { Password = "private-replacement" }).ToString())
            .IsEqualTo("RespireOptions { <redacted> }");
        await Assert.That(options.Password).IsEqualTo(password);
        await Assert.That(options.SentinelPassword).IsEqualTo(password);
    }

    [Test]
    public async Task OptionsFormattingDoesNotFormatOrInvokeNestedCredentials()
    {
        var options = new RespireOptions
        {
            CredentialProvider = new UnformattableProvider(),
            SentinelCredentialProvider = new UnformattableProvider(),
            TlsOptions = new UnformattableTlsOptions(),
            SentinelTlsOptions = new UnformattableTlsOptions(),
        };

        await Assert.That(options.ToString()).IsEqualTo("RespireOptions { <redacted> }");
    }

    [Test]
    public async Task MigrateOptionsFormattingRedactsCredentials()
    {
        var migrate = new RespireMigrateOptions { Username = "private-user", Password = "private-migrate-password" };

        await Assert.That(migrate.ToString()).IsEqualTo("RespireMigrateOptions { <redacted> }");
        await Assert.That(migrate.Password).IsEqualTo("private-migrate-password");
    }

    [Test]
    public async Task ConnectionOptionsFormattingRedactsCredentials()
    {
        var connection = new RespireConnectionOptions { Username = "private-user", Password = "private-connection-password" };

        await Assert.That(connection.ToString()).IsEqualTo("RespireConnectionOptions { <redacted> }");
        await Assert.That(connection.Password).IsEqualTo("private-connection-password");
    }

    [Test]
    public async Task AwsProviderFormattingDoesNotExposeCredentials()
    {
        var credentials = new SessionAWSCredentials("private-access-key", "private-secret-key", "private-session-token");
        object[] providers =
        [
            new ElastiCacheIamCredentialProvider(credentials, RegionEndpoint.USEast1, "cache", "private-user"),
            new MemoryDbIamCredentialProvider(credentials, RegionEndpoint.USEast1, "cluster", "private-user"),
        ];

        foreach (var provider in providers)
            await Assert.That(provider.ToString()).IsEqualTo(provider.GetType().FullName);
    }

    private sealed class UnformattableProvider : IRespireCredentialProvider
    {
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Formatting must not acquire credentials.");

        public override string ToString() => throw new InvalidOperationException("Formatting must not inspect providers.");
    }

    private sealed class UnformattableTlsOptions : SslClientAuthenticationOptions
    {
        public override string ToString() => throw new InvalidOperationException("Formatting must not inspect TLS options.");
    }
}
