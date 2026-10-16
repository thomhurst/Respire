using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardGroupTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InspectionPagesDistinctGroupsAndCountsWorkWithoutMutatingIndexes(int protocol)
    {
        await using var root = await RespireClient.ConnectAsync(
            RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var client = root.WithKeyPrefix("tenant:");
        var queue = new RespireJobQueue<string>(client, "inspection", Encoding.UTF8.GetBytes);
        await Assert.That((await RespireJobGroups.ReadAsync(client, queue.StreamKey)).Groups.Count).IsEqualTo(0);
        await queue.EnqueueAsync("ungrouped", "payload");
        await Assert.That((await RespireJobGroups.ReadAsync(client, queue.StreamKey)).Groups.Count).IsEqualTo(0);
        string[] names = ["a", "aa", "tenant:{other}:é/東京\0😀", "<script>group</script>"];
        foreach (var name in names)
        {
            await queue.EnqueueScheduledAsync(name + "-ready", "payload", new() { Group = name });
            await queue.EnqueueScheduledAsync(name + "-later", "payload", new() { Group = name, Delay = TimeSpan.FromHours(1) });
        }
        // One group occurs in both indexes and one has only a retained lease.
        await SeedLease(client, queue.StreamKey, "a", false);
        await SeedLease(client, queue.StreamKey, "lease-only", true);
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await RespireJobGroups.ReadAsync(client, queue.StreamKey, 2, cursor);
            await Assert.That(page.Groups.Count <= 2).IsTrue();
            foreach (var group in page.Groups)
            {
                seen.Add(group.Name);
                await Assert.That(group.QueuedCount).IsEqualTo(group.Name == "lease-only" ? 0L : 2L);
                await Assert.That(group.ReadyCount).IsEqualTo(group.Name == "lease-only" ? 0L : 1L);
                await Assert.That(group.ActiveCount).IsEqualTo(group.Name == "a" ? 1L : 0L);
                await Assert.That(group.IsEligible).IsEqualTo(group.Name != "lease-only");
            }
            cursor = page.NextCursor;
            if (seen.Count > 5) throw new InvalidOperationException("Pagination repeated a group.");
        } while (cursor is not null);
        await Assert.That(seen.Count).IsEqualTo(5);
        await Assert.That(seen.Distinct().Count()).IsEqualTo(5);
        foreach (var name in names) await Assert.That(seen.Contains(name)).IsTrue();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(8);
        var leases = await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('ZCARD', KEYS[1])"),
            [queue.StreamKey + ":group-leases"], []);
        await Assert.That(leases).IsEqualTo(2);
        await Assert.That((await RespireJobGroups.ReadAsync(root, queue.StreamKey)).Groups.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NamedInspectionReportsPoliciesAndIndependentAdmissionReasons()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client, "policy", Encoding.UTF8.GetBytes);
        await queue.SetGroupOptionsAsync("idle", new() { MaxConcurrency = 2, LeaseDuration = TimeSpan.FromSeconds(30) });
        await queue.PauseGroupAsync("idle");
        var idle = await RespireJobGroups.GetAsync(client, queue.StreamKey, "idle");
        await Assert.That(idle.IsPaused).IsTrue();
        await Assert.That(idle.IsEligible).IsFalse();
        await Assert.That(idle.MaxConcurrency).IsEqualTo(2);
        await Assert.That(idle.LeaseDuration).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(await RespireJobGroups.ResumeAsync(client, queue.StreamKey, "idle")).IsTrue();
        await Assert.That(await queue.ResumeGroupAsync("idle")).IsFalse();
        await queue.SetGroupOptionsAsync("limited", new()
        {
            MaxConcurrency = 1, RateLimit = new() { PermitLimit = 2, Window = TimeSpan.FromHours(1) },
        });
        await queue.EnqueueScheduledAsync("job", "payload", new() { Group = "limited" });
        await SeedLease(client, queue.StreamKey, "limited", false);
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("""
            local clock = redis.call('TIME')
            redis.call('HSET', KEYS[1], ARGV[1], tonumber(clock[1])*1000 + 7200000)
            return 1
            """), [queue.StreamKey + ":group-policy"], [Prefix("limited") + "tat"]);
        var limited = await RespireJobGroups.GetAsync(client, queue.StreamKey, "limited");
        await Assert.That(limited.IsConcurrencyLimited).IsTrue();
        await Assert.That(limited.IsRateLimited).IsTrue();
        await Assert.That(limited.IsEligible).IsFalse();
        await Assert.That(limited.RateLimit!.PermitLimit).IsEqualTo(2);
        await Assert.That(limited.RateLimit.Window).IsEqualTo(TimeSpan.FromHours(1));
        var options = new RespireDashboardOptions();
        options.Streams.Add(new() { Name = "Policy", Stream = queue.StreamKey, Group = RespireJobQueue<string>.ConsumerGroup, IsJobQueue = true });
        var html = DashboardPage.Render(await new DashboardState(client, options).ReadAsync(default), "/workers", "token", "value");
        await Assert.That(html).Contains("Rate limited");
        await Assert.That(html).Contains("Concurrency limited");
        await Assert.That(html).Contains("Concurrency: 1");
        await Assert.That(html).Contains("2 / 3600 seconds (burst)");
        await queue.SetGroupOptionsAsync("limited", null);
        await Assert.That((await RespireJobGroups.GetAsync(client, queue.StreamKey, "limited")).IsEligible).IsTrue();
        var absent = await RespireJobGroups.GetAsync(client, queue.StreamKey, "missing");
        await Assert.That(absent.QueuedCount).IsEqualTo(0);
        await Assert.That(absent.MaxConcurrency).IsNull();
        await Assert.That(absent.IsPaused).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GroupPagesEscapeContentPaginateAndPauseResumeWithVerifiedForms(bool nested)
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        const string stream = "{dash}:source";
        const string name = "<script>{other}/é&\"\0😀";
        await SeedLease(client, stream, name, false);
        await SeedLease(client, stream, "zzz", false);
        await using var app = await DashboardSecurityTests.CreateApplication(client, nested: nested, jobs: true, entryLimit: 1);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var path = nested ? "/tenant/admin/workers" : "/workers";
        using var home = await http.GetAsync(path + "/");
        var homeHtml = await home.Content.ReadAsStringAsync();
        await Assert.That(homeHtml).Contains("Application job groups");
        await Assert.That(homeHtml).Contains("Consumer groups");
        await Assert.That(homeHtml).Contains("Next groups");
        await Assert.That(homeHtml).DoesNotContain("<td>zzz</td>");
        var next = WebUtility.HtmlDecode(Regex.Match(homeHtml, "href=\"([^\"]+)\">Next groups").Groups[1].Value);
        using var nextPage = await http.GetAsync(next);
        var nextHtml = await nextPage.Content.ReadAsStringAsync();
        await Assert.That(nextHtml).Contains("<td>zzz</td>");
        await Assert.That(nextHtml).DoesNotContain("Next groups");
        using var page = await http.GetAsync(path + "/0/groups?group=" + Uri.EscapeDataString(name));
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(page.Headers.Contains("Content-Security-Policy")).IsTrue();
        await Assert.That(html).DoesNotContain("<script>");
        await Assert.That(html).Contains("&lt;script&gt;");
        await Assert.That(html).Contains("action=\"" + path + "/0/pause-group\"");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        var groupToken = Regex.Match(html, "name=\"groupToken\" value=\"([^\"]+)\"").Groups[1].Value;
        http.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        foreach (var action in new[] { "pause-group", "pause-group", "resume-group" })
        {
            using var response = await http.PostAsync(path + "/0/" + action, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["groupToken"] = groupToken, ["__RequestVerificationToken"] = token,
            }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo(path + "/0/groups?group=" + Uri.EscapeDataString(name));
            var info = await RespireJobGroups.GetAsync(client, stream, name);
            await Assert.That(info.IsPaused).IsEqualTo(action == "pause-group");
            using var updated = await http.GetAsync(path + "/");
            await Assert.That(await updated.Content.ReadAsStringAsync()).Contains(action == "pause-group" ? "Resume group" : "Pause group");
        }
        await Assert.That((await RespireJobGroups.GetAsync(root, stream, name)).IsPaused).IsFalse();
    }

    [Test]
    [Arguments("/workers/0/groups?group=", true, 400)]
    [Arguments("/workers/0/groups?group=a&group=b", true, 400)]
    [Arguments("/workers/0/groups?group=a&cursor=61%7C", true, 400)]
    [Arguments("/workers/0/groups?cursor=not-a-cursor", true, 400)]
    [Arguments("/workers/0/groups?cursor=61%7C&cursor=62%7C", true, 400)]
    [Arguments("/workers/99/groups", true, 404)]
    [Arguments("/workers/-1/groups", true, 404)]
    [Arguments("/workers/0/groups", false, 404)]
    public async Task GroupEndpointsRejectInvalidInputAndUnconfiguredQueues(string path, bool jobs, int status)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var app = await DashboardSecurityTests.CreateApplication(client, jobs: jobs);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var response = await http.GetAsync(path);
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
    }

    [Test]
    public async Task GroupReadErrorsAreRedactedAndEmptyQueuesExplainUngroupedWork()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await using var app = await DashboardSecurityTests.CreateApplication(client, jobs: true);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var empty = await http.GetAsync("/workers/0/groups");
        await Assert.That(await empty.Content.ReadAsStringAsync()).Contains("only ungrouped jobs");
        await client.Strings.SetAsync("{dash}:source:group-policy", "private wrong-type value");
        using var response = await http.GetAsync("/workers/0/groups?group=idle");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(html).DoesNotContain("WRONGTYPE");
        await Assert.That(html).DoesNotContain("private wrong-type value");
    }

    private static string Prefix(string name) => Convert.ToHexString(Encoding.UTF8.GetBytes(name)).ToLowerInvariant() + "|";

    [Test]
    [Arguments("hset")]
    [Arguments("zadd")]
    public async Task GroupControlPermissionFailureLeavesPolicyUnchanged(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var username = "dashboard-group-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">dashboard-group-password", "~*", "+@all", "-" + denied]);
        try
        {
            await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = username, Password = "dashboard-group-password" });
            await using var app = await DashboardSecurityTests.CreateApplication(client, jobs: true);
            using var http = app.GetTestClient();
            http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
            using var page = await http.GetAsync("/workers/0/groups?group=tenant");
            var html = await page.Content.ReadAsStringAsync();
            var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            http.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
            using var response = await http.PostAsync("/workers/0/pause-group", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["group"] = "tenant", ["__RequestVerificationToken"] = token,
            }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Ask an administrator");
            await Assert.That((await RespireJobGroups.GetAsync(admin, "{dash}:source", "tenant")).IsPaused).IsFalse();
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    [Arguments("", 0, true, 400)]
    [Arguments("tenant", 99, true, 404)]
    [Arguments("tenant", 0, false, 404)]
    public async Task VerifiedGroupPostsStillValidateNameAndQueue(string name, int index, bool jobs, int status)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.AddAsync("{dash}:dead", ("payload", "p"));
        await using var app = await DashboardSecurityTests.CreateApplication(client, jobs: jobs);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var page = await http.GetAsync("/workers/");
        var html = await page.Content.ReadAsStringAsync();
        http.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        using var response = await http.PostAsync("/workers/" + index + "/pause-group", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["group"] = name, ["__RequestVerificationToken"] = token,
        }));
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That((await RespireJobGroups.GetAsync(client, "{dash}:source", "tenant")).IsPaused).IsFalse();
    }

    private static async Task SeedLease(IRespireClient client, string stream, string name, bool expired)
    {
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("""
            local time = redis.call('TIME')
            local expiry = ARGV[2] == '1' and 0 or tonumber(time[1])*1000 + 3600000
            return redis.call('ZADD', KEYS[1], 0, ARGV[1] .. string.format('%016.0f', expiry) .. '|1-0')
            """), [stream + ":group-leases"], [Prefix(name), expired ? 1 : 0]);
    }
}
