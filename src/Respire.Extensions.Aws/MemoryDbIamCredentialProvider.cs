using Amazon;
using Amazon.Runtime;
using Respire;

namespace Respire.Extensions.Aws;

/// <summary>Creates renewable IAM authentication tokens for Amazon MemoryDB.</summary>
/// <remarks>
/// MemoryDB IAM authentication requires an IAM-enabled user. The AWS credentials provider
/// remains owned by the caller.
/// </remarks>
public sealed class MemoryDbIamCredentialProvider : IRespireCredentialProvider
{
    private readonly AWSCredentials _awsCredentials;
    private readonly RegionEndpoint _region;
    private readonly string _clusterName;
    private readonly string _username;

    /// <param name="awsCredentials">Caller-owned AWS credentials or credentials provider.</param>
    /// <param name="region">AWS region containing the cluster.</param>
    /// <param name="clusterName">MemoryDB cluster name.</param>
    /// <param name="username">IAM-enabled MemoryDB username.</param>
    public MemoryDbIamCredentialProvider(
        AWSCredentials awsCredentials,
        RegionEndpoint region,
        string clusterName,
        string username)
    {
        ArgumentNullException.ThrowIfNull(awsCredentials);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        _awsCredentials = awsCredentials;
        _region = region;
        _clusterName = clusterName;
        _username = username;
    }

    /// <inheritdoc />
    public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        => AwsIamCredentialProvider.CreateAsync(
            _awsCredentials, _region, "memorydb", _clusterName, _username, false,
            DateTimeOffset.UtcNow, cancellationToken);
}
