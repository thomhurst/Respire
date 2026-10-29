using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class JobQueueIntegrationTests
{
    [Test]
    [Arguments(RespireJobFlowFailurePolicy.FailParent, false, false)]
    [Arguments(RespireJobFlowFailurePolicy.FailParent, false, true)]
    [Arguments(RespireJobFlowFailurePolicy.FailParent, true, false)]
    [Arguments(RespireJobFlowFailurePolicy.Ignore, true, false)]
    [Arguments(RespireJobFlowFailurePolicy.Continue, true, false)]
    public async Task NestedFlowWaitsForChildrenAndAppliesFailurePolicy(RespireJobFlowFailurePolicy policy, bool fail, bool retry)
    {
        await using var root = await ConnectAsync();
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        var state = new FlowHandlerState { Fail = fail, Retry = retry };
        var tree = new RespireJobFlow<string>("root", "root")
        {
            Children = [new("branch", "branch") { Children = [new("leaf", "leaf"), new("sibling", "sibling")] }],
        };
        await Assert.That(await queue.EnqueueFlowAsync("flow", tree, policy)).IsTrue();
        await Assert.That(await queue.EnqueueFlowAsync("flow", new("replacement", "replacement"))).IsFalse();
        await Assert.That(await queue.GetFlowResultsAsync("missing")).IsEmpty();
        await Assert.That(await root.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(2);
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(state);
        services.AddRespireJobQueue<FlowHandler, string>("test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ConsumerCount = 2, DeliveryLimit = 3, ReadWait = TimeSpan.FromMilliseconds(20) },
            retry ? new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(20) } : null);
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await UntilAsync(async () => (await queue.GetFlowResultsAsync("flow"))[0].Status is "completed" or "failed", deadline.Token);
            var results = await queue.GetFlowResultsAsync("flow");
            var failsParent = fail && policy == RespireJobFlowFailurePolicy.FailParent;
            await Assert.That(results[0].Status).IsEqualTo(failsParent ? "failed" : "completed");
            await Assert.That(state.Children.ContainsKey("root")).IsEqualTo(!failsParent);
            if (!failsParent)
            {
                var children = state.Children["branch"];
                await Assert.That(children.Count).IsEqualTo(fail && policy == RespireJobFlowFailurePolicy.Ignore ? 1 : 2);
                var sibling = children.Single(child => child.Id == "sibling");
                await Assert.That(sibling.Result!).IsEquivalentTo(new byte[] { 0, 255, 13, 10 });
                if (fail && policy == RespireJobFlowFailurePolicy.Continue)
                    await Assert.That(children.Single(child => child.Id == "leaf").Status).IsEqualTo("failed");
                await Assert.That(state.Children["root"].Single().Status).IsEqualTo("completed");
            }
            else
            {
                var events = await client.Streams.ReadAsync(queue.EventsStreamKey);
                await Assert.That(events.Count(entry => entry.GetString("reason") == "child-failed")).IsEqualTo(2);
                await using var reader = queue.ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
                RespireJobEvent terminal;
                do
                {
                    await Assert.That(await reader.MoveNextAsync()).IsTrue();
                    terminal = reader.Current;
                } while (terminal.JobId != "root");
                await Assert.That(terminal.Kind).IsEqualTo("dead-letter");
                await Assert.That(terminal.Reason).IsEqualTo("child-failed");
                await Assert.That(terminal.EntryId.Value).IsEqualTo("0-0");
                await Assert.That(terminal.Attempt).IsEqualTo(0);
                await Assert.That(terminal.FlowKey).IsNotNull();
            }
        }
        finally { await worker.StopAsync(deadline.Token); }
        // Both successful and failed terminal trees can be removed through a prefixed client.
        await Assert.That(await queue.RemoveFlowAsync("flow")).IsTrue();
        await Assert.That(await queue.RemoveFlowAsync("flow")).IsFalse();
        await Assert.That(await queue.GetFlowResultsAsync("flow")).IsEmpty();
        await Assert.That(await queue.EnqueueFlowAsync("flow", tree, policy)).IsTrue();
    }

    [Test]
    public async Task FlowSizeBoundaryAndRemovalPreserveActiveDeliveries()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        // One-byte ID plus payload exactly fills the 256 KiB input allowance.
        await Assert.That(await queue.EnqueueFlowAsync("flow", new("r", new string('x', 262143)))).IsTrue();
        await Assert.That(async () => await queue.RemoveFlowAsync("flow")).Throws<RespireServerException>();
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var entry = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "consumer", new() { Count = 1 })).Single();
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, entry.GetString("_respire.job.flow")!];
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys,
            [RespireJobQueue<string>.ConsumerGroup, "consumer", entry.Id.Value, 1, "completed", "", "", 1, new byte[4097]]))
            .Throws<RespireServerException>();
        await Assert.That((await queue.GetFlowResultsAsync("flow")).Single().Status).IsEqualTo("ready");
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
            .IsEqualTo(1);
        await Assert.That(async () => await queue.RemoveFlowAsync("flow")).Throws<RespireServerException>();
        var result = Enumerable.Repeat((byte)255, 4096).ToArray();
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys,
            [RespireJobQueue<string>.ConsumerGroup, "consumer", entry.Id.Value, 1, "completed", "", "", 1, result])).IsEqualTo(1);
        await Assert.That((await queue.GetFlowResultsAsync("flow")).Single().Result!).IsEquivalentTo(result);
        var removed = await Task.WhenAll(queue.RemoveFlowAsync("flow").AsTask(), queue.RemoveFlowAsync("flow").AsTask());
        await Assert.That(removed.Count(value => value)).IsEqualTo(1);
        await Assert.That(await queue.GetFlowResultsAsync("flow")).IsEmpty();
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    public async Task UnsupportedFlowVersionFailsWithoutMutation(int version)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var tree = new RespireJobFlow<string>("parent", "parent") { Children = [new("leaf", "leaf")] };
        await queue.EnqueueFlowAsync("flow", tree);
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var entry = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "consumer", new() { Count = 1 })).Single();
        var key = entry.GetString("_respire.job.flow")!;
        var changeVersion = RespireScript.Create("""
            local flow = cmsgpack.unpack(redis.call('GET', KEYS[1]))
            flow.version = tonumber(ARGV[1])
            if flow.version == 0 then flow.version = nil end
            return redis.call('SET', KEYS[1], cmsgpack.pack(flow))
            """);
        using (await client.Scripts.ExecuteAsync(changeVersion, [key], [version])) { }
        await Assert.That(async () => await queue.GetFlowResultsAsync("flow")).Throws<RespireServerException>();
        await Assert.That(async () => await queue.EnqueueFlowAsync("flow", tree)).Throws<RespireServerException>();
        await Assert.That(async () => await queue.RemoveFlowAsync("flow")).Throws<RespireServerException>();
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, key];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "consumer", entry.Id.Value, 1, "completed"];
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys, args))
            .Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
            .IsEqualTo(1);
        using (await client.Scripts.ExecuteAsync(changeVersion, [key], [1])) { }
        await Assert.That((await queue.GetFlowResultsAsync("flow"))[0].Status).IsEqualTo("waiting");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys, args)).IsEqualTo(1);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, true)]
    public async Task FlowRecoveryFencesOldConsumerAndLostCompletionReply(int protocol, bool delete)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.EnqueueFlowAsync("flow", new("parent", "parent") { Children = [new("leaf", "leaf")] });
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var leaf = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "crashed", new() { Count = 1 })).Single();
        // Simulate a consumer crash before completion, then reclaim with the same name.
        using (var claimed = await client.ExecuteAsync("XCLAIM", [queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "crashed", 0, leaf.Id.Value])) { }
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, leaf.GetString("_respire.job.flow")!];
        var script = JobScript(delete ? "CompleteAndDelete" : "Complete");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "crashed", leaf.Id.Value, 1, "completed", "", "", 1, "stale"]))
            .IsEqualTo(0);
        await Assert.That((await queue.GetFlowResultsAsync("flow"))[0].Status).IsEqualTo("waiting");
        await using (var recovered = await ConnectAsync(protocol))
            await Assert.That(await recovered.Scripts.ExecuteIntegerAsync(script, keys,
                [RespireJobQueue<string>.ConsumerGroup, "crashed", leaf.Id.Value, 2, "completed", "", "", 1, new byte[] { 0, 255 }]))
                .IsEqualTo(1);
        // No client-side follow-up is needed to activate the parent, even if the reply is lost.
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "crashed", leaf.Id.Value, 2, "completed"]))
            .IsEqualTo(0);
        var parent = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "restart", new() { Count = 10 })).Single();
        await Assert.That(parent.GetString("_respire.job.id")).IsEqualTo("parent");
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(1);
        var results = await queue.GetFlowResultsAsync("flow");
        await Assert.That(results[1].Result!).IsEquivalentTo(new byte[] { 0, 255 });
        await Assert.That(results[0].Status).IsEqualTo("ready");
    }

    [Test]
    [Arguments("set")]
    [Arguments("xadd")]
    [Arguments("xdel")]
    [Arguments("xack")]
    public async Task DeniedFlowCompletionLeavesChildAndParentRecoverable(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueFlowAsync("flow", new("parent", "parent") { Children = [new("leaf", "leaf")] });
        await admin.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var leaf = (await admin.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "consumer", new() { Count = 1 })).Single();
        var username = "flow-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">flow-password", "~*", "+@all", "-" + denied]);
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, leaf.GetString("_respire.job.flow")!];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "consumer", leaf.Id.Value, 1, "completed"];
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = username, Password = "flow-password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys, args))
                .Throws<RespireServerException>();
            await Assert.That((await queue.GetFlowResultsAsync("flow"))[0].Status).IsEqualTo("waiting");
            await Assert.That(await admin.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(1);
            await Assert.That(await admin.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys, args)).IsEqualTo(1);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    [Arguments("set")]
    [Arguments("xadd")]
    [Arguments("xdel")]
    public async Task DeniedFlowEnqueueDoesNotReserveIdOrAppendLeaves(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var username = "flow-enqueue-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">flow-password", "~*", "+@all", "-" + denied]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = username, Password = "flow-password" });
            var tree = new RespireJobFlow<string>("parent", "parent") { Children = [new("leaf", "leaf")] };
            await Assert.That(async () => await Queue(restricted).EnqueueFlowAsync("flow", tree)).Throws<RespireServerException>();
            var queue = Queue(admin);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            await Assert.That(await queue.GetFlowResultsAsync("flow")).IsEmpty();
            await Assert.That(await queue.EnqueueFlowAsync("flow", tree)).IsTrue();
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    public async Task FlowNativeAckFailureRollsBackResultParentActivationAndEvent()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var info = await admin.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        if (Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]) < new Version(8, 2)) return;
        var queue = Queue(admin);
        await queue.EnqueueFlowAsync("flow", new("parent", "parent") { Children = [new("leaf", "leaf")] });
        await admin.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var leaf = (await admin.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "consumer", new() { Count = 1 })).Single();
        var username = "flow-ack-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">flow-password", "~*", "+@all", "-xackdel"]);
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, leaf.GetString("_respire.job.flow")!];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "consumer", leaf.Id.Value, 1, "completed", "", "", 1, "result"];
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = username, Password = "flow-password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(JobScript("CompleteAndDelete"), keys, args))
                .Throws<RespireServerException>();
            var results = await queue.GetFlowResultsAsync("flow");
            await Assert.That(results[0].Status).IsEqualTo("waiting");
            await Assert.That(results[1].Result).IsNull();
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(1);
            await Assert.That(await admin.Scripts.ExecuteIntegerAsync(JobScript("CompleteAndDelete"), keys, args)).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    public async Task ConcurrentFlowSubmissionsAndChildCompletionsActivateOneParent()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var tree = new RespireJobFlow<string>("parent", "parent")
        { Children = Enumerable.Range(0, 16).Select(i => new RespireJobFlow<string>("child-" + i, "payload")).ToArray() };
        var enqueued = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => queue.EnqueueFlowAsync("flow", tree).AsTask()));
        await Assert.That(enqueued.Count(value => value)).IsEqualTo(1);
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var leaves = await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "consumer", new() { Count = 32 });
        await Assert.That(leaves.Length).IsEqualTo(16);
        await Task.WhenAll(leaves.Select(leaf => client.Scripts.ExecuteIntegerAsync(JobScript("Complete"),
            [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, leaf.GetString("_respire.job.flow")!],
            [RespireJobQueue<string>.ConsumerGroup, "consumer", leaf.Id.Value, 1, "completed"]).AsTask()));
        var parents = await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "consumer", new() { Count = 32 });
        await Assert.That(parents.Length).IsEqualTo(1);
        await Assert.That(parents[0].GetString("_respire.job.id")).IsEqualTo("parent");
    }

    public sealed class FlowHandlerState
    {
        public bool Fail { get; init; }
        public bool Retry { get; init; }
        public int LeafAttempts;
        public ConcurrentDictionary<string, IReadOnlyList<RespireJobFlowResult>> Children { get; } = new();
    }

    public sealed class FlowHandler(FlowHandlerState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            if (job.Id == "leaf" && state.Retry && Interlocked.Increment(ref state.LeafAttempts) == 1)
                return job.RetryAfter(TimeSpan.FromMilliseconds(20));
            state.Children[job.Id] = await job.GetChildResultsAsync(cancellationToken);
            return state.Fail && job.Id == "leaf" ? RespireStreamWorkerResult.DeadLetter : job.Complete([0, 255, 13, 10]);
        }
    }
}
