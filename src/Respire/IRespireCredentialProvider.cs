using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Respire;

/// <summary>Supplies authentication credentials for new connections and proactive renewal.</summary>
/// <remarks>
/// Providers are caller-owned and may be called concurrently by different physical connections.
/// Implementations must honor cancellation and must not log passwords or access tokens.
/// Respire does not dispose the provider. Cache token acquisition inside the provider if needed.
/// Return the same ACL user for every connection throughout a client's lifetime. Renewal rejects
/// a user change; create a new client when changing identity.
/// </remarks>
public interface IRespireCredentialProvider
{
    /// <summary>Gets current credentials. An expiry enables proactive connection re-authentication.</summary>
    ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Immutable authentication credentials with an optional absolute UTC expiry.</summary>
public sealed class RespireCredentials
{
    /// <summary>Creates credentials. A null username selects Redis's default user.</summary>
    public RespireCredentials(string? username, string password, DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        Username = username;
        Password = password;
        ExpiresAt = expiresAt;
    }

    /// <summary>ACL username, or null for the default user.</summary>
    public string? Username { get; }

    /// <summary>Password or access token. Treat this value as a secret.</summary>
    public string Password { get; }

    /// <summary>Expiry of these credentials, or null when no proactive renewal is needed.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    internal bool IsSameAs(RespireCredentials other)
        => ExpiresAt == other.ExpiresAt && Username == other.Username
            && CryptographicOperations.FixedTimeEquals(
                MemoryMarshal.AsBytes(Password.AsSpan()), MemoryMarshal.AsBytes(other.Password.AsSpan()));

    /// <summary>Returns a diagnostic representation without credential material.</summary>
    public override string ToString() => "RespireCredentials { <redacted> }";
}
