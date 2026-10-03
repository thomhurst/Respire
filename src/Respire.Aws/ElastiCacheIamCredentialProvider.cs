using Amazon;
using Amazon.Runtime;
using Respire;

namespace Respire.Aws;

/// <summary>Creates renewable IAM authentication tokens for Amazon ElastiCache.</summary>
/// <remarks>
/// ElastiCache IAM authentication requires in-transit encryption and an IAM-enabled user whose
/// user ID matches its username. Serverless caches use the serverless cache name; provisioned
/// deployments use the replication group ID. Cache names must be lowercase. The AWS credentials
/// provider remains owned by the caller.
/// </remarks>
public sealed class ElastiCacheIamCredentialProvider : IRespireCredentialProvider
{
    private readonly AWSCredentials _awsCredentials;
    private readonly RegionEndpoint _region;
    private readonly string _cacheName;
    private readonly string _userId;
    private readonly bool _isServerless;

    /// <param name="awsCredentials">Caller-owned AWS credentials or credentials provider.</param>
    /// <param name="region">AWS region containing the cache.</param>
    /// <param name="cacheName">Serverless cache name or provisioned replication group ID.</param>
    /// <param name="userId">IAM-enabled ElastiCache user ID (equal to its username).</param>
    /// <param name="isServerless">Whether the cache is serverless.</param>
    public ElastiCacheIamCredentialProvider(
        AWSCredentials awsCredentials,
        RegionEndpoint region,
        string cacheName,
        string userId,
        bool isServerless = false)
    {
        ArgumentNullException.ThrowIfNull(awsCredentials);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!string.Equals(cacheName, cacheName.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("ElastiCache cache names must be lowercase.", nameof(cacheName));

        _awsCredentials = awsCredentials;
        _region = region;
        _cacheName = cacheName;
        _userId = userId;
        _isServerless = isServerless;
    }

    /// <inheritdoc />
    public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        => AwsIamCredentialProvider.CreateAsync(
            _awsCredentials, _region, "elasticache", _cacheName, _userId, _isServerless,
            DateTimeOffset.UtcNow, cancellationToken);
}
