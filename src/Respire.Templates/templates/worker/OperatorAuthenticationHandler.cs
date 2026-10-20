using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RespireWorker;

// A small, explicit operator login for this starter. Replace with your identity provider
// before deployment, retaining the QueueOperators policy and dashboard antiforgery checks.
public sealed class OperatorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration, IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OperatorBasic";

    private bool AllowsCredentials => Request.IsHttps || (environment.IsDevelopment()
        && Context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Plain HTTP is permitted only for a loopback request in Development.
        if (!AllowsCredentials)
            return Task.FromResult(AuthenticateResult.Fail("HTTPS is required."));

        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
            || !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
            return Task.FromResult(AuthenticateResult.NoResult());

        byte[] supplied;
        try { supplied = Convert.FromBase64String(header.Parameter); }
        catch (FormatException) { return Task.FromResult(AuthenticateResult.Fail("Invalid credentials.")); }

        var password = configuration["Dashboard:Password"];
        if (string.IsNullOrWhiteSpace(password)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(supplied),
                SHA256.HashData(Encoding.UTF8.GetBytes($"operator:{password}"))))
            return Task.FromResult(AuthenticateResult.Fail("Invalid credentials."));

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Name, "operator"),
            new Claim(ClaimTypes.Role, "QueueOperator")
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Do not invite the browser to send a password over an untrusted HTTP connection.
        if (!AllowsCredentials)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"RespireWorker\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}
