using System.Text.Json;
using Azure.Core;

namespace Respire.Azure;

/// <summary>Gets Microsoft Entra access tokens for Azure Managed Redis.</summary>
/// <remarks>
/// The supplied credential is caller-owned and is not disposed by this provider. The Redis
/// username is the object ID of the managed identity or service principal granted Redis access.
/// It can be supplied explicitly or read from the first access token's <c>oid</c> claim and cached
/// for this provider's lifetime. Token acquisition uses the Azure Managed Redis resource scope.
/// </remarks>
public sealed class RespireAzureManagedRedisCredentialProvider : IRespireCredentialProvider
{
    /// <summary>The Microsoft Entra scope for Azure Managed Redis.</summary>
    public const string Scope = "https://redis.azure.com/.default";

    private readonly TokenCredential _credential;
    private string? _username;

    /// <summary>Creates a provider that reads the Redis user object ID from the first access token's <c>oid</c> claim.</summary>
    /// <remarks>The resolved username is shared by all connections and token renewals using this provider.</remarks>
    public RespireAzureManagedRedisCredentialProvider(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
    }

    /// <summary>Creates a provider using a caller-owned Azure token credential and Redis user object ID.</summary>
    public RespireAzureManagedRedisCredentialProvider(TokenCredential credential, string username)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        _credential = credential;
        _username = username;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No username was supplied and the token has no usable <c>oid</c> claim.</exception>
    public async ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var requestContext = new TokenRequestContext([Scope]);
        var accessToken = await _credential
            .GetTokenAsync(requestContext, cancellationToken)
            .ConfigureAwait(false);

        var username = Volatile.Read(ref _username);
        if (username is null)
        {
            var resolvedUsername = ReadUsername(accessToken.Token);
            username = Interlocked.CompareExchange(ref _username, resolvedUsername, null) ?? resolvedUsername;
        }

        return new RespireCredentials(username, accessToken.Token, accessToken.ExpiresOn);
    }

    private static string ReadUsername(string token)
    {
        var parts = token.Split('.');
        if (parts.Length == 3)
        {
            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("oid", out var claim)
                    && claim.ValueKind == JsonValueKind.String
                    && claim.GetString() is { } username
                    && !string.IsNullOrWhiteSpace(username))
                {
                    return username;
                }
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                // Do not expose token contents through parser exceptions or their inner exceptions.
            }
        }

        throw new InvalidOperationException(
            "Cannot resolve the Redis username from the access token. Expected a JWT with a non-empty string 'oid' claim. "
            + "Use the constructor with an explicit username to supply the identity's object ID.");
    }
}
