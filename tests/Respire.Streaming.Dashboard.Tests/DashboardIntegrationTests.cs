using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardIntegrationTests(RedisTestContainer redis)
{
    [Test]
    public async Task JobQueueSnapshotAndPageExposeControlsWithoutLeakingUnavailableState()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client.WithKeyPrefix("tenant:"), "dashboard", System.Text.Encoding.UTF8.GetBytes);
        await queue.PauseAsync();
        var view = client.WithKeyPrefix("tenant:");
        await view.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('HINCRBY', KEYS[1], 'hits', 7)"),
            [queue.ControlKey], []);
        var options = new RespireDashboardOptions();
        options.Streams.Add(new() { Name = "jobs", Stream = queue.StreamKey, Group = RespireJobQueue<string>.ConsumerGroup, IsJobQueue = true });
        var snapshot = await new DashboardState(view, options).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].IsPaused).IsTrue();
        await Assert.That(snapshot.Streams[0].RateLimitHits).IsEqualTo(7);
        var html = DashboardPage.Render(snapshot, "/dashboard", "token", "value");
        await Assert.That(html).Contains("Paused");
        await Assert.That(html).Contains("Limiter hits</dt><dd>7");
        await view.Scripts.ExecuteIntegerAsync(RespireScript.Create("redis.call('DEL', KEYS[1]); redis.call('SET', KEYS[1], 'wrong-type'); return 1"),
            [queue.ControlKey], []);
        snapshot = await new DashboardState(view, options).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].IsPaused).IsNull();
        await Assert.That(snapshot.Streams[0].RateLimitHits).IsNull();
        await Assert.That(snapshot.Streams[0].Error!).Contains("Queue controls unavailable");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WorkerGeneratedDeadLetterCanBeRetriedAndConsumed(bool jobs)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRespireClient>(client);
        var queue = new RespireJobQueue<string>(client, "dashboard", System.Text.Encoding.UTF8.GetBytes);
        var stream = jobs ? queue.StreamKey : "{dash}:source";
        var dead = jobs ? queue.DeadLetterStreamKey : "{dash}:dead";
        var group = jobs ? RespireJobQueue<string>.ConsumerGroup : "g";
        var workerOptions = new RespireStreamWorkerOptions { DeadLetterStream = dead, ReadWait = TimeSpan.FromMilliseconds(50) };
        if (jobs)
        {
            services.AddRespireJobQueue<RejectJob, string>("dashboard", System.Text.Encoding.UTF8.GetBytes,
                System.Text.Encoding.UTF8.GetString, workerOptions);
            await queue.EnqueueAsync("job-7", "original");
        }
        else
        {
            services.AddRespireStreamWorker<RejectEntry, RespireStreamEntry>(stream, group, entry => entry, workerOptions);
            await client.Streams.AddAsync(stream, ("payload", "original"));
        }
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(deadline.Token);
        RespireStreamEntry deadEntry;
        try
        {
            await using var entries = client.Streams.ReadAllAsync(dead, cancellationToken: deadline.Token).GetAsyncEnumerator();
            await Assert.That(await entries.MoveNextAsync()).IsTrue();
            deadEntry = entries.Current;
        }
        finally { await worker.StopAsync(deadline.Token); }
        var options = new RespireDashboardOptions();
        options.Streams.Add(new() { Name = "worker", Stream = stream, Group = group, DeadLetterStream = dead, IsJobQueue = jobs });
        var state = new DashboardState(client, options);
        await Assert.That(await state.MutateAsync(0, deadEntry.Id.ToString(), true, deadline.Token)).IsEqualTo(1);
        var retried = (await client.Streams.ReadGroupOnceAsync(stream, group, "dashboard-retry", cancellationToken: deadline.Token)).Single();
        await Assert.That(retried.GetString(jobs ? "_respire.job.payload" : "payload")).IsEqualTo("original");
        if (jobs) await Assert.That(retried.GetString("_respire.job.id")).IsEqualTo("job-7");
        await Assert.That(await retried.AckAsync(deadline.Token)).IsTrue();
        await Assert.That(await client.Streams.CountAsync(dead)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DeadLetteredBatchMemberRetriesIndependentlyOfRetainedBatch(bool removeBatch, bool rejectRetry)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client, "dashboard-batch", System.Text.Encoding.UTF8.GetBytes);
        await queue.CreateBatchAsync(new() { OnComplete = new("callback", "callback") }, "batch");
        await queue.AddBatchJobAsync("batch", "member", "original");
        await queue.SealBatchAsync("batch");
        var handler = new RetryBatchMember { RejectRetry = rejectRetry };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRespireClient>(client);
        services.AddSingleton(handler);
        services.AddRespireJobQueue<RetryBatchMember, string>("dashboard-batch", System.Text.Encoding.UTF8.GetBytes,
            System.Text.Encoding.UTF8.GetString, new() { ReadWait = TimeSpan.FromMilliseconds(50) });
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(deadline.Token);
        try
        {
            await using var events = queue.ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
            while (await events.MoveNextAsync())
                if (events.Current.JobId == "callback" && events.Current.Kind == "completed") break;
            var original = (await queue.GetBatchStatusAsync("batch"))!;
            await Assert.That(original.IsCompleted).IsTrue();
            await Assert.That(original.Failed).IsEqualTo(1);
            if (removeBatch) await Assert.That(await queue.RemoveBatchAsync("batch")).IsTrue();
            var dead = (await client.Streams.RangeAsync(queue.DeadLetterStreamKey)).Single();
            var options = new RespireDashboardOptions();
            options.Streams.Add(new() { Name = "jobs", Stream = queue.StreamKey,
                Group = RespireJobQueue<string>.ConsumerGroup, DeadLetterStream = queue.DeadLetterStreamKey, IsJobQueue = true });
            await Assert.That(await new DashboardState(client, options).MutateAsync(0, dead.Id.ToString(), true, deadline.Token)).IsEqualTo(1);
            var retried = (await client.Streams.RangeAsync(queue.StreamKey)).Last();
            await Assert.That(retried.GetString("_respire.job.batch")).IsNull();
            await Assert.That(retried.GetString("_respire.job.member")).IsNull();
            await Assert.That(retried.GetString("_respire.job.id")).IsEqualTo("member");
            await Assert.That(retried.GetString("_respire.job.payload")).IsEqualTo("original");
            while (await events.MoveNextAsync())
                if (events.Current.JobId == "member" && events.Current.Kind == (rejectRetry ? "dead-letter" : "completed")) break;
            await Assert.That(handler.MemberCalls).IsEqualTo(2);
            await Assert.That(handler.CallbackCalls).IsEqualTo(1);
            await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(rejectRetry ? 1 : 0);
            var status = await queue.GetBatchStatusAsync("batch");
            if (removeBatch) await Assert.That(status).IsNull();
            else
            {
                await Assert.That(status!.IsCompleted).IsTrue();
                await Assert.That(status.Failed).IsEqualTo(1);
                await Assert.That(status.Pending + status.Succeeded + status.Cancelled).IsEqualTo(0);
            }
            // The original member, callback, and independent retry are the only publications.
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(3);
            var groups = await client.Streams.GroupInfoAsync(queue.StreamKey);
            await Assert.That(groups.Single().Pending).IsEqualTo(0);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    public sealed class RetryBatchMember : IRespireStreamHandler<RespireJob<string>>
    {
        public int MemberCalls;
        public int CallbackCalls;
        public bool RejectRetry;
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            if (job.Id == "callback") Interlocked.Increment(ref CallbackCalls);
            else if (Interlocked.Increment(ref MemberCalls) == 1 || RejectRetry)
                return ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }

    public sealed class RejectEntry : IRespireStreamHandler<RespireStreamEntry>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry entry, CancellationToken cancellationToken)
            => ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter);
    }

    public sealed class RejectJob : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
            => ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter);
    }

    [Test]
    [Arguments("xadd")]
    [Arguments("xdel")]
    public async Task RetryPermissionFailureLeavesBothStreamsUntouched(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        await admin.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        var id = await AddDead(admin);
        var username = "dashboard-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">dashboard-test-password", "~*", "+@all", "-" + denied]);
        try
        {
            await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = username, Password = "dashboard-test-password" });
            var state = new DashboardState(client, DashboardSecurityTests.Options());
            await Assert.That(async () => await state.MutateAsync(0, id.ToString(), true, default)).Throws<RespireServerException>();
            await using var app = await DashboardSecurityTests.CreateApplication(client);
            using var http = app.GetTestClient();
            http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
            using var page = await http.GetAsync("/workers/");
            var html = await page.Content.ReadAsStringAsync();
            var token = System.Text.RegularExpressions.Regex.Match(html,
                "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            http.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
            using var response = await http.PostAsync("/workers/0/retry", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = id.ToString(), ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(token),
            }));
            await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Forbidden);
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Ask an administrator");
            await Assert.That(await admin.Streams.CountAsync("{dash}:source")).IsEqualTo(0);
            await Assert.That(await admin.Streams.CountAsync("{dash}:dead")).IsEqualTo(1);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AuthorizedPostRetriesWithAntiforgeryToken(bool nested)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        var id = await AddDead(client);
        await using var app = await DashboardSecurityTests.CreateApplication(client, nested: nested);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var path = nested ? "/tenant/admin/workers" : "/workers";
        using var page = await http.GetAsync(path + "/");
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("action=\"" + path + "/0/retry\"");
        var token = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        http.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        using var response = await http.PostAsync(path + "/0/retry", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(token),
        }));
        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.SeeOther);
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo(path + "/");
        await Assert.That(await client.Streams.CountAsync("{dash}:source")).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("{dash}:dead")).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentRetriesPreserveBinaryFieldsAndConsumeDeadLetterOnce()
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        using (await root.ExecuteAsync("XADD", ["tenant:{dash}:dead", "1-0", "_respire.source_id", "9-0",
            "_respire.group", "g", "_respire.attempt", "3", "_respire.reason", "explicit", "_respire.exception_type", "",
            new byte[] { 255, 0 }, new byte[] { 0, 255, 13, 10 }, "_respire.source_id", "original",
            "_respire.job.flow", "application-flow", "_respire.job.node", "application-node",
            "_respire.job.flow", new byte[] { 0, 255 },
            "_respire.job.batch", "application-batch", "_respire.job.member", "application-member"])) { }
        var state = new DashboardState(client, DashboardSecurityTests.Options());
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => state.MutateAsync(0, "1-0", true, default).AsTask()));
        await Assert.That(results.Sum()).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("{dash}:dead")).IsEqualTo(0);
        using var rows = await root.ExecuteAsync("XRANGE", ["tenant:{dash}:source", "-", "+"]);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0][1][0].AsBytes().SequenceEqual(new byte[] { 255, 0 })).IsTrue();
        await Assert.That(rows[0][1][1].AsBytes().SequenceEqual(new byte[] { 0, 255, 13, 10 })).IsTrue();
        await Assert.That(rows[0][1][2].AsString()).IsEqualTo("_respire.source_id");
        await Assert.That(rows[0][1][3].AsString()).IsEqualTo("original");
        await Assert.That(rows[0][1].Count).IsEqualTo(14);
        await Assert.That(rows[0][1][4].AsString()).IsEqualTo("_respire.job.flow");
        await Assert.That(rows[0][1][5].AsString()).IsEqualTo("application-flow");
        await Assert.That(rows[0][1][6].AsString()).IsEqualTo("_respire.job.node");
        await Assert.That(rows[0][1][7].AsString()).IsEqualTo("application-node");
        await Assert.That(rows[0][1][8].AsString()).IsEqualTo("_respire.job.flow");
        await Assert.That(rows[0][1][9].AsBytes().SequenceEqual(new byte[] { 0, 255 })).IsTrue();
        await Assert.That(rows[0][1][10].AsString()).IsEqualTo("_respire.job.batch");
        await Assert.That(rows[0][1][11].AsString()).IsEqualTo("application-batch");
        await Assert.That(rows[0][1][12].AsString()).IsEqualTo("_respire.job.member");
        await Assert.That(rows[0][1][13].AsString()).IsEqualTo("application-member");
    }

    [Test]
    public async Task RetryRejectsForeignMetadataAndDeleteNeverPublishes()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        var foreign = await AddDead(client, "other");
        var state = new DashboardState(client, DashboardSecurityTests.Options());
        await Assert.That(await state.MutateAsync(0, foreign.ToString(), true, default)).IsEqualTo(-1);
        await Assert.That(await state.MutateAsync(0, foreign.ToString(), false, default)).IsEqualTo(-1);
        await Assert.That(await client.Streams.CountAsync("{dash}:dead")).IsEqualTo(1);
        var valid = await AddDead(client);
        await Assert.That(await state.MutateAsync(0, valid.ToString(), false, default)).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("{dash}:source")).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync("{dash}:dead")).IsEqualTo(1);
    }

    [Test]
    public async Task MissingSourceGroupCannotLoseDeadLetter()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.AddAsync("{dash}:source", ("payload", "other"));
        var id = await AddDead(client);
        var state = new DashboardState(client, DashboardSecurityTests.Options());
        await Assert.That(await state.MutateAsync(0, id.ToString(), true, default)).IsEqualTo(-1);
        await Assert.That(await client.Streams.CountAsync("{dash}:dead")).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("{dash}:source")).IsEqualTo(1);
        var snapshot = (await state.ReadAsync(default)).Streams.Single();
        await Assert.That(snapshot.Error).IsNull();
        await Assert.That(snapshot.Pending.Length).IsEqualTo(0);
        await Assert.That(snapshot.DeadLetters.Length).IsEqualTo(1);
    }

    [Test]
    public async Task UnavailableDeadLettersDoNotHideSourceInspection()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        await client.Streams.AddAsync("{dash}:source", ("payload", "test"));
        using (await client.ExecuteAsync("SET", ["{dash}:dead", "wrong-type"])) { }
        var snapshot = await new DashboardState(client, DashboardSecurityTests.Options()).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].Length).IsEqualTo(1);
        await Assert.That(snapshot.Streams[0].Groups.Length).IsEqualTo(1);
        var html = DashboardPage.Render(snapshot, "/workers", "token", "value");
        await Assert.That(html).Contains("Dead-letter inspection unavailable");
        await Assert.That(html).DoesNotContain("No dead letters.");
    }

    [Test]
    public async Task MissingSourceStillAllowsDeadLetterInspectionAndDeletion()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var id = await AddDead(client);
        var state = new DashboardState(client, DashboardSecurityTests.Options());
        var snapshot = await state.ReadAsync(default);
        await Assert.That(snapshot.Streams[0].Error).IsNotNull();
        await Assert.That(snapshot.Streams[0].DeadLetters.Length).IsEqualTo(1);
        var html = DashboardPage.Render(snapshot, "/workers", "token", "value");
        await Assert.That(html).Contains("/0/delete");
        await Assert.That(html).DoesNotContain("/0/retry");
        await Assert.That(async () => await state.MutateAsync(0, id.ToString(), true, default)).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync("{dash}:dead")).IsEqualTo(1);
        await Assert.That(await state.MutateAsync(0, id.ToString(), false, default)).IsEqualTo(1);
        await Assert.That((await state.ReadAsync(default)).Streams[0].DeadLetters.Length).IsEqualTo(0);
    }

    [Test]
    public async Task JobRetryRetainsIdentityAndResetsAttemptExpiryAndReplacementFence()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client, "dashboard", System.Text.Encoding.UTF8.GetBytes);
        var options = new RespireDashboardOptions();
        options.Streams.Add(new() { Name = "jobs", Stream = queue.StreamKey,
            Group = RespireJobQueue<string>.ConsumerGroup, DeadLetterStream = queue.DeadLetterStreamKey, IsJobQueue = true });
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var id = await client.Streams.AddAsync(queue.DeadLetterStreamKey,
            ("_respire.source_id", "1-0"), ("_respire.group", RespireJobQueue<string>.ConsumerGroup),
            ("_respire.attempt", "3"), ("_respire.reason", "explicit"), ("_respire.exception_type", ""),
            ("_respire.job.id", "job-42"), ("_respire.job.payload", "data"),
            ("_respire.job.attempts", "9"), ("_respire.job.expires", "1"),
            ("_respire.job.deduplication", "Replace"), ("_respire.job.generation", "terminal"),
            ("_respire.job.revision", "old"));
        await Assert.That(await new DashboardState(client, options).MutateAsync(0, id.ToString(), true, default)).IsEqualTo(1);
        var entry = (await client.Streams.RangeAsync(queue.StreamKey)).Single();
        await Assert.That(entry.GetString("_respire.job.id")).IsEqualTo("job-42");
        await Assert.That(entry.GetString("_respire.job.payload")).IsEqualTo("data");
        await Assert.That(entry.GetString("_respire.job.attempts")).IsEqualTo("0");
        await Assert.That(entry.GetString("_respire.job.expires")).IsEqualTo("0");
        await Assert.That(entry.GetString("_respire.job.generation")).IsNull();
        await Assert.That(entry.GetString("_respire.job.revision")).IsNull();
        await Assert.That(entry.GetString("_respire.job.deduplication")).IsEqualTo("Separate lifecycle");
    }

    [Test]
    public async Task InspectionBoundsEntriesAndMeasuresDeliveryRate()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        for (var i = 0; i < 5; i++)
        {
            await client.Streams.AddAsync("{dash}:source", ("payload", "test"));
            await AddDead(client);
        }
        var options = DashboardSecurityTests.Options();
        options.EntryLimit = 2;
        var clock = new Clock();
        var state = new DashboardState(client, options, clock);
        await client.Streams.ReadGroupOnceAsync("{dash}:source", "g", "worker", new RespireStreamReadOptions { Count = 1 });
        var initial = await state.ReadAsync(default);
        var first = initial.Streams[0];
        await Assert.That(first.DeliveriesPerSecond).IsNull();
        await client.Streams.ReadGroupOnceAsync("{dash}:source", "g", "worker", new RespireStreamReadOptions { Count = 5 });
        var shared = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => state.ReadAsync(default)));
        await Assert.That(shared.All(snapshot => ReferenceEquals(initial, snapshot))).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(2));
        var early = (await state.ReadAsync(default)).Streams[0];
        await Assert.That(early.Groups.Single().Pending).IsEqualTo(5);
        await Assert.That(early.DeliveriesPerSecond).IsNull();
        clock.Advance(TimeSpan.FromSeconds(3));
        var second = (await state.ReadAsync(default)).Streams[0];
        await Assert.That(second.Pending.Length).IsEqualTo(2);
        await Assert.That(second.DeadLetters.Length).IsEqualTo(2);
        await Assert.That(second.Length).IsEqualTo(5);
        await Assert.That(second.DeadLetterLength).IsEqualTo(5);
        await Assert.That(second.Groups.Single().Pending).IsEqualTo(5);
        await Assert.That(second.DeliveriesPerSecond).IsEqualTo(0.8);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private static ValueTask<RespireStreamId> AddDead(IRespireClient client, string group = "g")
        => client.Streams.AddAsync("{dash}:dead", ("_respire.source_id", "1-0"), ("_respire.group", group),
            ("_respire.attempt", "3"), ("_respire.reason", "explicit"), ("_respire.exception_type", ""), ("payload", "test"));
}
