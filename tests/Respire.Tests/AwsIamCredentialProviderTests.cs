using Amazon;
using Amazon.Runtime;
using Respire.Extensions.Aws;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public sealed class AwsIamCredentialProviderTests
{
    private static readonly DateTimeOffset SignedAt = new(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);
    private static readonly AWSCredentials Credentials = new BasicAWSCredentials("access-key", "secret-key");

    [Test]
    [Arguments("elasticache", "cache.example.amazonaws.com", "alice@example.com", false)]
    [Arguments("elasticache", "cache.example.amazonaws.com", "alice@example.com", true)]
    [Arguments("memorydb", "cluster.example.memorydb.amazonaws.com", "redis-user", false)]
    public async Task SigningPreservesServiceHostQueryAndExpiry(
        string service, string resource, string username, bool serverless)
    {
        var credentials = await AwsIamCredentialProvider.CreateAsync(
            Credentials, RegionEndpoint.USEast1, service, resource, username, serverless,
            SignedAt, CancellationToken.None);

        await Assert.That(credentials.Username).IsEqualTo(username);
        await Assert.That(credentials.ExpiresAt).IsEqualTo(SignedAt.AddMinutes(15));
        await Assert.That(credentials.Password.StartsWith(resource + "/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(credentials.Password.StartsWith(resource + ":80/", StringComparison.Ordinal)).IsFalse();
        await Assert.That(credentials.Password).Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256");

        var query = credentials.Password[(credentials.Password.IndexOf('?') + 1)..];
        var decodedQuery = Uri.UnescapeDataString(query);
        await Assert.That(decodedQuery).Contains("Action=connect");
        await Assert.That(decodedQuery).Contains($"User={username}");
        await Assert.That(decodedQuery).Contains($"X-Amz-Credential=access-key/20261001/us-east-1/{service}/aws4_request");
        await Assert.That(decodedQuery.Contains("ResourceType=ServerlessCache", StringComparison.Ordinal))
            .IsEqualTo(serverless);
    }
}
