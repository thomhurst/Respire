using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Coordination;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.RateLimiting.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class RateLimitingIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments("fixed")]
    [Arguments("sliding")]
    [Arguments("bucket")]
    public async Task TwoApplicationsShareLimitAndReturnRetryAfter(string algorithm)
    {
        await using var firstRedis = await RespireClient.ConnectAsync(redis.ConnectionString);
        await using var secondRedis = await RespireClient.ConnectAsync(redis.ConnectionString);
        var key = $"http:{Guid.NewGuid():N}";
        void Configure(RateLimiterOptions options, RespireClient client)
        {
            var limiters = new RespireCoordination(client).RateLimiters;
            switch (algorithm)
            {
                case "fixed":
                    options.AddRespireFixedWindowLimiter("api", limiters, key, 2, TimeSpan.FromMinutes(5));
                    break;
                case "sliding":
                    options.AddRespireSlidingWindowLimiter("api", limiters, key, 2, TimeSpan.FromMinutes(5), 4);
                    break;
                default:
                    options.AddRespireTokenBucketLimiter("api", limiters, key, 2, 1, TimeSpan.FromMinutes(5));
                    break;
            }
        }

        await using var firstApp = new RateLimitingApplication(options => Configure(options, firstRedis));
        await using var secondApp = new RateLimitingApplication(options => Configure(options, secondRedis));
        using var first = firstApp.CreateClient();
        using var second = secondApp.CreateClient();

        using var firstAllowed = await first.GetAsync("/api");
        using var secondAllowed = await second.GetAsync("/api");
        using var firstDenied = await first.GetAsync("/api");
        using var secondDenied = await second.GetAsync("/api");
        await Assert.That(firstAllowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(secondAllowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(firstDenied.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(secondDenied.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(firstDenied.Headers.RetryAfter?.Delta > TimeSpan.Zero).IsTrue();
        await Assert.That(secondDenied.Headers.RetryAfter?.Delta > TimeSpan.Zero).IsTrue();
    }

    [Test]
    public async Task UserPartitionsShareAcrossApplicationsButRemainIndependent()
    {
        await using var firstRedis = await RespireClient.ConnectAsync(redis.ConnectionString);
        await using var secondRedis = await RespireClient.ConnectAsync(redis.ConnectionString);
        var prefix = $"http:users:{Guid.NewGuid():N}:";
        void Configure(RateLimiterOptions options, RespireClient client)
        {
            var limiters = new RespireCoordination(client).RateLimiters;
            options.AddRespirePartitionedLimiter("api",
                context => context.Request.Headers["X-User"].ToString(),
                user => limiters.FixedWindow(prefix + user, 1, TimeSpan.FromMinutes(5)));
        }

        await using var firstApp = new RateLimitingApplication(options => Configure(options, firstRedis));
        await using var secondApp = new RateLimitingApplication(options => Configure(options, secondRedis));
        using var first = firstApp.CreateClient();
        using var second = secondApp.CreateClient();
        first.DefaultRequestHeaders.Add("X-User", "alice");
        second.DefaultRequestHeaders.Add("X-User", "alice");
        using var aliceAllowed = await first.GetAsync("/api");
        using var aliceDenied = await second.GetAsync("/api");
        second.DefaultRequestHeaders.Remove("X-User");
        second.DefaultRequestHeaders.Add("X-User", "bob");
        using var bobAllowed = await second.GetAsync("/api");
        await Assert.That(aliceAllowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(aliceDenied.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(bobAllowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}

// WebApplicationFactory uses this entry assembly but builds each host from the supplied configuration.
public sealed class RateLimitingApplication(Action<RateLimiterOptions> configure)
    : WebApplicationFactory<RateLimitingApplication>
{
    protected override IHostBuilder CreateHostBuilder() => Host.CreateDefaultBuilder()
        .ConfigureWebHost(web => web
            .UseSetting("TEST_CONTENTROOT_RESPIRE_RATELIMITING_TESTS", AppContext.BaseDirectory)
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddRateLimiter(configure);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/api", () => "OK").RequireRateLimiting("api");
                    endpoints.MapGet("/other", () => "OK").RequireRateLimiting("other");
                    endpoints.MapGet("/unlimited", () => "OK");
                });
            }));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseContentRoot(AppContext.BaseDirectory);
}
