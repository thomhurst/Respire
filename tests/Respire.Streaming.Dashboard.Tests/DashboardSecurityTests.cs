using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardSecurityTests(RedisTestContainer redis)
{
    [Test]
    public async Task UnknownPolicyNeverServesDashboard()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var app = await CreateApplication(client, "unknown-policy");
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        await Assert.That(async () => await http.GetAsync("/workers/")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RendererEscapesNamesPathsTokensAndTruncatesPayloads()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.AddAsync("{dash}:dead", ("<field>", new string('z', 1000) + "hidden-tail"));
        var config = Options().Streams[0];
        config.Name = "<script>name</script>";
        config.Group = "<group>";
        var snapshot = new DashboardSnapshot([new DashboardStreamSnapshot(0, config, 1, 1,
            [new("<group>", 1, 0, "1-0", 0, 0)], [], await client.Streams.RangeAsync("{dash}:dead"), null, null)]);
        var html = DashboardPage.Render(snapshot, "/tenant/workers", "token\"name", "<token>");
        await Assert.That(html).DoesNotContain("<script>name</script>");
        await Assert.That(html).Contains("&lt;script&gt;name&lt;/script&gt;");
        await Assert.That(html).Contains("&lt;group&gt;");
        await Assert.That(html).Contains("&lt;field&gt;");
        await Assert.That(html).Contains("/tenant/workers/0/retry");
        await Assert.That(html).Contains("&lt;token&gt;");
        await Assert.That(html).DoesNotContain("hidden-tail");
    }

    [Test]
    public async Task RegistrationFreezesTheAllowedStreams()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRespireClient>(client);
        RespireDashboardOptions? captured = null;
        services.AddRespireStreamingDashboard(options =>
        {
            captured = options;
            options.Streams.Add(Options().Streams[0]);
        });
        captured!.Streams[0].Stream = "unregistered-secret";
        captured.Streams.Clear();
        await using var provider = services.BuildServiceProvider();
        var snapshot = await provider.GetRequiredService<DashboardState>().ReadAsync(default);
        await Assert.That(snapshot.Streams.Single().Configuration.Stream).IsEqualTo("{dash}:source");
        await Assert.That(snapshot.Streams.Single().Error).IsNull();
    }

    [Test]
    [Arguments("GET", "/workers/", null, 401)]
    [Arguments("GET", "/workers/0/logs?jobId=job", null, 401)]
    [Arguments("GET", "/workers/0/logs?jobId=job", "reader", 403)]
    [Arguments("GET", "/workers/0/groups", null, 401)]
    [Arguments("GET", "/workers/0/groups?group=a", "reader", 403)]
    [Arguments("POST", "/workers/0/groups", null, 401)]
    [Arguments("POST", "/workers/0/groups", "reader", 403)]
    [Arguments("POST", "/workers/0/groups", "operator", 400)]
    [Arguments("POST", "/workers/0/pause-group", null, 401)]
    [Arguments("POST", "/workers/0/resume-group", null, 401)]
    [Arguments("POST", "/workers/0/pause-group", "reader", 403)]
    [Arguments("POST", "/workers/0/resume-group", "reader", 403)]
    [Arguments("POST", "/workers/0/pause-group", "operator", 400)]
    [Arguments("POST", "/workers/0/resume-group", "operator", 400)]
    [Arguments("POST", "/workers/0/retry", null, 401)]
    [Arguments("POST", "/workers/0/delete", null, 401)]
    [Arguments("GET", "/workers/", "reader", 403)]
    [Arguments("POST", "/workers/0/retry", "reader", 403)]
    [Arguments("POST", "/workers/0/delete", "reader", 403)]
    [Arguments("POST", "/workers/0/retry", "operator", 400)]
    [Arguments("POST", "/workers/0/delete", "operator", 400)]
    public async Task EveryEndpointRequiresPolicyAndMutationsRequireAntiforgery(string method, string path, string? role, int status)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var app = await CreateApplication(client);
        using var http = app.GetTestClient();
        if (role is not null) http.DefaultRequestHeaders.Add("X-Test-Role", role);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "1-0" });
        using var response = await http.SendAsync(request);
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
    }

    [Test]
    public async Task InspectionEncodesPayloadAndTokensGuardValidPosts()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        await client.Streams.AddAsync("{dash}:dead", ("payload", "<script>alert(1)</script>"));
        await using var app = await CreateApplication(client);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var page = await http.GetAsync("/workers/");
        var html = await page.Content.ReadAsStringAsync();
        if (Environment.GetEnvironmentVariable("RESPIRE_DASHBOARD_PREVIEW") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, html);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).DoesNotContain("<script>alert(1)</script>");
        await Assert.That(html).Contains("&lt;script&gt;");
        await Assert.That(page.Headers.CacheControl!.NoStore).IsTrue();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        await Assert.That(token.Length > 0).IsTrue();
        http.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        using var response = await http.PostAsync("/workers/99/retry", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "1-0", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Return to dashboard");
        using var invalidId = await http.PostAsync("/workers/0/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "-", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
        }));
        await Assert.That(invalidId.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task MissingPolicyIsRejectedAtMapping()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        await Assert.That(() => app.MapRespireStreamingDashboard("/workers", " ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task UnsafeConfigurationsAreRejected()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var options = Options();
        options.Streams[0].DeadLetterStream = "{other}:dead";
        await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentException>();
        options = Options();
        options.Streams.Add(new() { Name = "ambiguous", Stream = "{dash}:other", Group = "g", DeadLetterStream = "{dash}:dead" });
        await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentException>();
        options = Options();
        options.EntryLimit = 201;
        await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentOutOfRangeException>();
    }

    internal static RespireDashboardOptions Options(bool jobs = false)
    {
        var options = new RespireDashboardOptions();
        options.Streams.Add(new() { Name = "Orders", Stream = "{dash}:source", Group = "g",
            DeadLetterStream = "{dash}:dead", IsJobQueue = jobs });
        return options;
    }

    internal static async Task<WebApplication> CreateApplication(IRespireClient client, string mappedPolicy = "operators", bool nested = false,
        bool jobs = false, int entryLimit = 50, bool kestrel = false, Action<RespireDashboardOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        if (kestrel) builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(client);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("operators", policy => policy.RequireRole("operator")));
        builder.Services.AddRespireStreamingDashboard(options =>
        {
            options.EntryLimit = entryLimit;
            options.Streams.Add(Options(jobs).Streams[0]);
            configure?.Invoke(options);
        });
        var app = builder.Build();
        if (nested) app.UsePathBase("/tenant");
        app.UseAuthentication();
        app.UseAuthorization();
        if (nested) app.MapGroup("/admin").MapRespireStreamingDashboard("/workers", mappedPolicy);
        else app.MapRespireStreamingDashboard("/workers", mappedPolicy);
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator"), new Claim(ClaimTypes.Role, role)], "test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}
