using Aspire.Respire;
using Azure.Core;
using Respire.Azure;
using Respire.Caching;
using Respire.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Aspire.Respire.Tests;

public class CredentialFormattingTests
{
    [Test]
    public async Task ConfigurationClassFormattingDoesNotExposeCredentials()
    {
        const string connectionString = "redis://private-user:private-password@localhost:6379";
        object[] configurations =
        [
            new RespireOptionsBuilder { Password = "private-password", SentinelPassword = "private-sentinel-password" },
            new RespireCacheRegistrationOptions { ConnectionString = connectionString },
            new RespireClientSettings { ConnectionString = connectionString },
            new RespireAzureManagedRedisCredentialProvider(new PrivateCredential(), "private-user"),
        ];

        foreach (var configuration in configurations)
            await Assert.That(configuration.ToString()).IsEqualTo(configuration.GetType().FullName);
    }

    private sealed class PrivateCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("private-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));

        public override string ToString() => "private-credential";
    }
}
