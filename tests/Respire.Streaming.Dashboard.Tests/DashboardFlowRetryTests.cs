using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardFlowRetryTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(false, false, RespireJobFlowFailurePolicy.FailParent)]
    [Arguments(false, true, RespireJobFlowFailurePolicy.FailParent)]
    [Arguments(true, false, RespireJobFlowFailurePolicy.FailParent)]
    [Arguments(true, true, RespireJobFlowFailurePolicy.FailParent)]
    [Arguments(false, false, RespireJobFlowFailurePolicy.Continue)]
    [Arguments(false, true, RespireJobFlowFailurePolicy.Continue)]
    [Arguments(true, false, RespireJobFlowFailurePolicy.Continue)]
    [Arguments(true, true, RespireJobFlowFailurePolicy.Continue)]
    public async Task TerminalFlowMemberRetriesIndependently(bool removeFlow, bool failRetry, RespireJobFlowFailurePolicy policy)
    {
        await using var root = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        var queue = new RespireJobQueue<string>(client, "flow-retry", Encoding.UTF8.GetBytes);
        await queue.EnqueueFlowAsync("flow", new("parent", "parent payload")
        {
            Children = [new("leaf", "leaf payload")],
        }, policy);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var original = await RunWorkerUntilAsync(client, queue, true, "parent", default, deadline.Token);
        await Assert.That(original.Kind).IsEqualTo(policy == RespireJobFlowFailurePolicy.FailParent ? "dead-letter" : "completed");
        var results = await queue.GetFlowResultsAsync("flow");
        await Assert.That(results.Single(node => node.Id == "leaf").Status).IsEqualTo("failed");
        var dead = (await client.Streams.RangeAsync(queue.DeadLetterStreamKey)).Single();
        var flowKey = dead.GetString("_respire.job.flow")!;
        byte[] originalFlow;
        using (var reply = await client.ExecuteAsync("GET", [flowKey])) originalFlow = reply.AsBytes().ToArray();
        if (removeFlow) await Assert.That(await queue.RemoveFlowAsync("flow")).IsTrue();
        var originalEntries = await client.Streams.CountAsync(queue.StreamKey);
        var originalEvents = await client.Streams.CountAsync(queue.EventsStreamKey);

        var options = new RespireDashboardOptions();
        options.Streams.Add(new()
        {
            Name = "jobs", Stream = queue.StreamKey, Group = RespireJobQueue<string>.ConsumerGroup,
            DeadLetterStream = queue.DeadLetterStreamKey, IsJobQueue = true,
        });
        await Assert.That(await new DashboardState(client, options).MutateAsync(0, dead.Id.ToString(), true, deadline.Token)).IsEqualTo(1);
        var retried = (await client.Streams.RangeAsync(queue.StreamKey)).Last();
        await Assert.That(retried.GetString("_respire.job.id")).IsEqualTo("leaf");
        await Assert.That(retried.GetString("_respire.job.payload")).IsEqualTo("leaf payload");
        await Assert.That(retried.GetString("_respire.job.flow")).IsNull();
        await Assert.That(retried.GetString("_respire.job.node")).IsNull();
        await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);

        var terminal = await RunWorkerUntilAsync(client, queue, failRetry, "leaf", original.Id, deadline.Token);
        await Assert.That(terminal.Kind).IsEqualTo(failRetry ? "dead-letter" : "completed");
        await Assert.That(terminal.EntryId).IsEqualTo(retried.Id);
        await Assert.That(terminal.FlowKey).IsNull();
        await Assert.That(terminal.Attempt).IsEqualTo(1);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count).IsEqualTo(0);
        // Neither outcome can activate a parent again or publish another dependency event.
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(originalEntries + 1);
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(originalEvents + 1);
        var remainingDead = await client.Streams.RangeAsync(queue.DeadLetterStreamKey);
        await Assert.That(remainingDead.Length).IsEqualTo(failRetry ? 1 : 0);
        if (failRetry)
        {
            await Assert.That(remainingDead[0].GetString("_respire.source_id")).IsEqualTo(retried.Id.ToString());
            await Assert.That(remainingDead[0].GetString("_respire.job.flow")).IsNull();
            await Assert.That(remainingDead[0].GetString("_respire.job.node")).IsNull();
        }
        using var retained = await client.ExecuteAsync("GET", [flowKey]);
        if (removeFlow) await Assert.That(retained.IsNull).IsTrue();
        else await Assert.That(retained.AsBytes().SequenceEqual(originalFlow)).IsTrue();
    }

    private static async Task<RespireJobEvent> RunWorkerUntilAsync(IRespireClient client, RespireJobQueue<string> queue,
        bool failLeaf, string lastJob, RespireStreamId after, CancellationToken token)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(client).AddSingleton(new Outcome(failLeaf));
        services.AddRespireJobQueue<FlowHandler, string>("flow-retry", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ReadWait = TimeSpan.FromMilliseconds(20) });
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(token);
        try
        {
            await foreach (var entry in queue.ReadEventsAsync(after, token))
                if (entry.JobId == lastJob) return entry;
            throw new InvalidOperationException("The worker did not publish its terminal event.");
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    public sealed record Outcome(bool FailLeaf);

    public sealed class FlowHandler(Outcome outcome) : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
            => ValueTask.FromResult(outcome.FailLeaf && job.Id == "leaf"
                ? RespireStreamWorkerResult.DeadLetter : job.Complete([0, 255, 13, 10]));
    }
}
