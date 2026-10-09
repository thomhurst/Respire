using System.Collections.Concurrent;
using Hangfire.Redis.StackExchange;
using Hangfire.Redis.Tests.Utils;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Hangfire.Redis.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public class ServerLifecycleTests(RedisTestContainer fixture) : UpstreamRedisTest(fixture)
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Completions = new();

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task WorkerExecutesStoredJobAndDisposesServer(bool useTransactions)
    {
        var storage = new RedisStorage(RedisUtils.Connection, new RedisStorageOptions
        {
            Db = RedisUtils.GetDb(),
            UseTransactions = useTransactions,
            FetchTimeout = TimeSpan.FromMilliseconds(50),
        });
        var key = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Completions.TryAdd(key, completion));
        try
        {
            var client = new BackgroundJobClient(storage);
            var jobId = client.Enqueue(() => CompleteJob(key));
            using (var server = new BackgroundJobServer(new BackgroundJobServerOptions
            {
                WorkerCount = 1,
                Queues = ["default"],
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            }, storage))
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
                server.SendStop();
            }

            using var connection = storage.GetConnection();
            Assert.Equal("Succeeded", connection.GetStateData(jobId).Name);
            var control = RedisUtils.CreateClient();
            Assert.Equal("Succeeded", (string?)await control.HashGetAsync($"{{hangfire}}:job:{jobId}", "State"));
            Assert.Equal(0, await control.ListLengthAsync("{hangfire}:queue:default:dequeued"));
            Assert.Empty(await control.SetMembersAsync("{hangfire}:servers"));
            Assert.Single(storage.GetMonitoringApi().SucceededJobs(0, 10));
            // The borrowed multiplexer remains usable after the worker shuts down.
            Assert.NotEqual(default, ((Hangfire.Storage.JobStorageConnection)connection).GetUtcDateTime());
        }
        finally
        {
            Completions.TryRemove(key, out _);
        }
    }

    public static void CompleteJob(string key) => Completions[key].TrySetResult();
}
