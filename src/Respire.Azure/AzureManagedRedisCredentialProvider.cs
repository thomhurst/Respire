using Azure.Core;

namespace Respire.Azure;

/// <summary>Gets Microsoft Entra access tokens for Azure Managed Redis.</summary>
/// <remarks>
/// The supplied credential is caller-owned and is not disposed by this provider. The Redis
/// username must be the object ID of the managed identity or service principal granted Redis
/// access. Token acquisition uses the Azure Managed Redis resource scope.
/// </remarks>
public sealed class AzureManagedRedisCredentialProvider : IRespireCredentialProvider
{
    /// <summary>The Microsoft Entra scope for Azure Managed Redis.</summary>
    public const string Scope = "https://redis.azure.com/.default";

    private readonly TokenCredential _credential;
    private readonly string _username;

    /// <summary>Creates a provider using a caller-owned Azure token credential and Redis user object ID.</summary>
    public AzureManagedRedisCredentialProvider(TokenCredential credential, string username)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        _credential = credential;
        _username = username;
    }

    /// <inheritdoc />
    public async ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var requestContext = new TokenRequestContext([Scope]);
        var accessToken = await _credential
            .GetTokenAsync(requestContext, cancellationToken)
            .ConfigureAwait(false);

        return new RespireCredentials(_username, accessToken.Token, accessToken.ExpiresOn);
    }
}
