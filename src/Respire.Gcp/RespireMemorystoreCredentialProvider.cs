using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;

namespace Respire.Gcp;

/// <summary>Gets renewable OAuth access tokens for Google Cloud Memorystore.</summary>
/// <remarks>
/// Supports application default credentials, including user, service account, Compute Engine,
/// impersonated, and external account credentials. The credential remains caller-owned.
/// Google caches and refreshes tokens; Respire uses their actual expiry to renew live connections.
/// Memorystore authenticates the default Redis user with the access token as its password.
/// </remarks>
public sealed class RespireMemorystoreCredentialProvider : IRespireCredentialProvider
{
    /// <summary>The OAuth scope required by Memorystore IAM authentication.</summary>
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly GoogleCredential _credential;

    /// <summary>Creates a provider using a caller-owned Google credential.</summary>
    /// <remarks>Fixed access tokens are unsupported because they cannot be refreshed.</remarks>
    public RespireMemorystoreCredentialProvider(GoogleCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        // Memorystore requires OAuth access tokens, not locally signed service account JWTs.
        if (credential.UnderlyingCredential is ServiceAccountCredential serviceAccount)
            credential = serviceAccount.WithUseJwtAccessWithScopes(false).ToGoogleCredential();
        _credential = credential.CreateScoped(Scope);
        if (_credential.UnderlyingCredential is not (ServiceCredential or UserCredential))
            throw new ArgumentException("A renewable Google OAuth credential is required.", nameof(credential));
    }

    /// <inheritdoc />
    public async ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ((ITokenAccess)_credential).GetAccessTokenForRequestAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Read one response snapshot so concurrent refresh cannot pair an old token with a new expiry.
        TokenResponse? token = _credential.UnderlyingCredential switch
        {
            ServiceCredential service => service.Token,
            UserCredential user => user.Token,
            _ => null,
        };
        if (token is null || string.IsNullOrEmpty(token.AccessToken) || token.ExpiresInSeconds is not > 0)
            throw new InvalidOperationException("Google did not return an access token with an expiry.");

        var expiry = new DateTimeOffset(token.IssuedUtc, TimeSpan.Zero).AddSeconds(token.ExpiresInSeconds.Value);
        return new RespireCredentials(null, token.AccessToken, expiry);
    }
}
