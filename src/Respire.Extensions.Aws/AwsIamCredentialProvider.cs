using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Signing;
using Respire;

namespace Respire.Extensions.Aws;

internal static class AwsIamCredentialProvider
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    internal static async ValueTask<RespireCredentials> CreateAsync(
        AWSCredentials awsCredentials,
        RegionEndpoint region,
        string service,
        string resource,
        string username,
        bool isElastiCacheServerless,
        DateTimeOffset signedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(awsCredentials);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        cancellationToken.ThrowIfCancellationRequested();

        var query = $"Action=connect&User={Uri.EscapeDataString(username)}";
        if (isElastiCacheServerless)
            query += "&ResourceType=ServerlessCache";

        var request = new AWSSigningRequest
        {
            HttpMethod = HttpMethod.Get,
            RequestUri = new UriBuilder(Uri.UriSchemeHttp, resource)
            {
                Path = "/",
                Query = query,
            }.Uri,
        };
        var parameters = new AWSSigV4Parameters
        {
            Credentials = awsCredentials,
            Region = region,
            Service = service,
            SignedAt = signedAt.UtcDateTime,
        };
        var signed = await AWSSigV4Signer.PresignAsync(
            request, parameters, TokenLifetime, cancellationToken).ConfigureAwait(false);
        var token = signed.Uri.GetComponents(UriComponents.Host, UriFormat.UriEscaped)
            + signed.Uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return new RespireCredentials(username, token, signedAt.Add(TokenLifetime));
    }
}
