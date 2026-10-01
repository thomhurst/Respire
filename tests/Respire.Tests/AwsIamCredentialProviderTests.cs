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
        // These are golden SigV4 vectors; update deliberately if the AWS signer changes canonicalization.
        var expectedSignature = service switch
        {
            "memorydb" => "0ec430b11027787fa361a6fd300bd4910e66af5f388cf5a1abddd183d9f29be5",
            _ when serverless => "e6a68f3652e7ea35c818a8052ad51287fa1a798738ff681d8edb21c80e74ef1e",
            _ => "521d632cb32842cddfb9fc358d9108121025f5c2be9e1fd75ed005dbefcd7a84",
        };
        await Assert.That(credentials.Password).Contains($"X-Amz-Signature={expectedSignature}");
        var decodedQuery = Uri.UnescapeDataString(query);
        await Assert.That(decodedQuery).Contains("Action=connect");
        await Assert.That(decodedQuery).Contains($"User={username}");
        await Assert.That(decodedQuery).Contains($"X-Amz-Credential=access-key/20261001/us-east-1/{service}/aws4_request");
        await Assert.That(decodedQuery.Contains("ResourceType=ServerlessCache", StringComparison.Ordinal))
            .IsEqualTo(serverless);
    }

    [Test]
    public async Task SigningIncludesSessionCredentials()
    {
        var sessionCredentials = new SessionAWSCredentials("access-key", "secret-key", "session-token");

        var credentials = await AwsIamCredentialProvider.CreateAsync(
            sessionCredentials, RegionEndpoint.USEast1, "memorydb", "cluster", "redis-user", false,
            SignedAt, CancellationToken.None);

        await Assert.That(credentials.Password).Contains("X-Amz-Security-Token=session-token");
        await Assert.That(credentials.Password).Contains("X-Amz-Credential=access-key%2F20261001%2Fus-east-1%2Fmemorydb%2Faws4_request");
        // Fixed signature proves the session token participates in the signed query.
        await Assert.That(credentials.Password).Contains("X-Amz-Signature=b79ecb133c9b1797ec77e8bc5e99b7a6b1848bc3b53c39eb6d7706d58420eeab");
    }

    [Test]
    public async Task PublicProvidersReturnFifteenMinuteCredentials()
    {
        var before = DateTimeOffset.UtcNow;
        var elasticache = new ElastiCacheIamCredentialProvider(
            Credentials, RegionEndpoint.USEast1, "cache", "elasticache-user", isServerless: true);
        var memorydb = new MemoryDbIamCredentialProvider(
            Credentials, RegionEndpoint.USEast1, "cluster", "memorydb-user");

        var cacheCredentials = await elasticache.GetCredentialsAsync();
        var memoryDbCredentials = await memorydb.GetCredentialsAsync();
        var after = DateTimeOffset.UtcNow;

        await Assert.That(cacheCredentials.Username).IsEqualTo("elasticache-user");
        await Assert.That(memoryDbCredentials.Username).IsEqualTo("memorydb-user");
        await Assert.That(cacheCredentials.ExpiresAt.HasValue).IsTrue();
        await Assert.That(memoryDbCredentials.ExpiresAt.HasValue).IsTrue();
        await Assert.That(cacheCredentials.ExpiresAt!.Value).IsGreaterThan(before.AddMinutes(14));
        await Assert.That(cacheCredentials.ExpiresAt.Value).IsLessThanOrEqualTo(after.AddMinutes(15));
        await Assert.That(memoryDbCredentials.ExpiresAt!.Value).IsGreaterThan(before.AddMinutes(14));
        await Assert.That(memoryDbCredentials.ExpiresAt.Value).IsLessThanOrEqualTo(after.AddMinutes(15));
        await Assert.That(cacheCredentials.Password).Contains("ResourceType=ServerlessCache");
        await Assert.That(memoryDbCredentials.Password).Contains("%2Fmemorydb%2Faws4_request");
    }

    [Test]
    public async Task ProvidersHonorPreCanceledTokenAndCredentialErrors()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new MemoryDbIamCredentialProvider(
            Credentials, RegionEndpoint.USEast1, "cluster", "redis-user");
        await Assert.That(async () => await provider.GetCredentialsAsync(cancellation.Token))
            .Throws<OperationCanceledException>();

        var failingProvider = new MemoryDbIamCredentialProvider(
            new ThrowingAwsCredentials(), RegionEndpoint.USEast1, "cluster", "redis-user");
        await Assert.That(async () => await failingProvider.GetCredentialsAsync())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ProviderConstructorsRejectInvalidResources()
    {
        await Assert.That(() => new ElastiCacheIamCredentialProvider(
            Credentials, RegionEndpoint.USEast1, "Uppercase", "user")).Throws<ArgumentException>();
        await Assert.That(() => new ElastiCacheIamCredentialProvider(
            Credentials, RegionEndpoint.USEast1, "cache", " ")).Throws<ArgumentException>();
        await Assert.That(() => new MemoryDbIamCredentialProvider(
            Credentials, RegionEndpoint.USEast1, "", "user")).Throws<ArgumentException>();
        await Assert.That(() => new MemoryDbIamCredentialProvider(
            Credentials, RegionEndpoint.USEast1, "cluster", " ")).Throws<ArgumentException>();
    }

    private sealed class ThrowingAwsCredentials : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials()
            => throw new InvalidOperationException("credential provider failed");
    }
}
