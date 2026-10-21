using Microsoft.AspNetCore.Http;

namespace Respire.Idempotency;

/// <summary>Controls buffering, retention, and caller isolation for HTTP idempotency.</summary>
public sealed class RespireIdempotencyOptions
{
    /// <summary>Returns a stable, trusted caller/tenant identity. Required; a missing or malformed UTF-16 identity rejects keyed requests with 400.</summary>
    public Func<HttpContext, string?>? CallerScope { get; set; }

    /// <summary>A nonblank, well-formed UTF-16 namespace unique to this application and response format. Invalid values fail startup validation. Defaults to respire:http-idempotency:v1:.</summary>
    public string KeyPrefix { get; set; } = "respire:http-idempotency:v1:";

    /// <summary>Lifetime of a pending claim and, from completion, its stored result. Defaults to 24 hours.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Whether to compare SHA-256 request body fingerprints. Defaults to false.</summary>
    public bool VerifyRequestBody { get; set; }

    /// <summary>Maximum fingerprinted request body size; larger requests receive 413. Defaults to 1 MiB.</summary>
    public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum buffered response body size; larger responses receive 500. Defaults to 1 MiB.</summary>
    public int MaxResponseBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Status codes to store. Defaults to 200 through 299. Other responses abandon the claim.</summary>
    public ISet<int> StoredStatusCodes { get; } = new HashSet<int>(Enumerable.Range(200, 100));
}
