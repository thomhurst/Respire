using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class JobQueueTests
{
    [Test]
    [Arguments(-1)]
    [Arguments(1441)]
    public async Task InvalidMetricsRetentionIsRejected(int retention)
    {
        var services = new ServiceCollection();
        await Assert.That(() => services.AddRespireJobQueue<JobQueueIntegrationTests.MetricsHandler, string>(
            "test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, null,
            new() { MetricsRetentionMinutes = retention })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GroupIsPartOfTheEnvelopeButDeliveryControlsAreNot()
    {
        var ungrouped = new RespireJob<string>("id", "payload", "1-0");
        var grouped = ungrouped with { Group = "tenant" };
        var equal = grouped with { };
        var different = grouped with { Group = "Tenant" };
        var jobs = new HashSet<RespireJob<string>> { ungrouped, grouped, equal, different };
        grouped.RetryAfter(TimeSpan.Zero);
        grouped.Complete([1]);
        await Assert.That(ungrouped.Group).IsNull();
        await Assert.That(grouped.Equals(equal)).IsTrue();
        await Assert.That(grouped.GetHashCode()).IsEqualTo(equal.GetHashCode());
        await Assert.That(jobs.Count).IsEqualTo(3);
        await Assert.That(jobs.Contains(grouped)).IsTrue();
    }

    [Test]
    public async Task InvalidGroupIsRejectedBeforeSerialization()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "queue", _ => throw new InvalidOperationException());
        foreach (var group in new[] { "", " ", "\t\r\n" })
            await Assert.That(async () => await queue.EnqueueScheduledAsync("id", "payload", new() { Group = group }))
                .Throws<ArgumentException>();
        foreach (var group in new[] { "\ud800", "\udc00" })
            await Assert.That(async () => await queue.EnqueueScheduledAsync("id", "payload", new() { Group = group }))
                .Throws<EncoderFallbackException>();
    }

    [Test]
    [Arguments(0, 1000L)]
    [Arguments(-1, 1000L)]
    [Arguments(1, 0L)]
    [Arguments(1, -1L)]
    [Arguments(1, 2147483648L)]
    public async Task InvalidRatePolicyIsRejectedBeforeRegistrationOrRedis(int permits, long milliseconds)
    {
        var policy = new RespireJobRateLimit { PermitLimit = permits, Window = TimeSpan.FromMilliseconds(milliseconds) };
        var services = new ServiceCollection();
        await Assert.That(() => services.AddRespireJobQueue<Handler, string>("queue", Encoding.UTF8.GetBytes,
            Encoding.UTF8.GetString, null, new() { RateLimit = policy })).Throws<ArgumentOutOfRangeException>();
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "queue", Encoding.UTF8.GetBytes);
        await Assert.That(async () => await queue.SetRateLimitAsync(policy)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OversizedFlowInputsAndResultsAreRejected()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "queue", Encoding.UTF8.GetBytes);
        // The budget counts UTF-8 bytes across all nodes, including IDs.
        var tree = new RespireJobFlow<string>("root", new string('é', 65536))
        { Children = [new("child", new string('é', 65536))] };
        await Assert.That(async () => await queue.EnqueueFlowAsync("flow", tree)).Throws<ArgumentException>();
        var job = new RespireJob<string>("root", "payload", new("1-0"));
        await Assert.That(() => job.Complete(new byte[4097])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(job.Complete(new byte[4096])).IsEqualTo(RespireStreamWorkerResult.Ack);
    }

    [Test]
    public async Task InvalidFlowTreesAreRejectedBeforeEnqueue()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "queue", Encoding.UTF8.GetBytes);
        var duplicate = new RespireJobFlow<string>("root", "payload") { Children = [new("root", "child")] };
        await Assert.That(async () => await queue.EnqueueFlowAsync("flow", duplicate)).Throws<ArgumentException>();
        var children = new List<RespireJobFlow<string>>();
        var cycle = new RespireJobFlow<string>("root", "payload") { Children = children };
        children.Add(cycle);
        await Assert.That(async () => await queue.EnqueueFlowAsync("flow", cycle)).Throws<ArgumentException>();
        var large = new RespireJobFlow<string>("root", "payload")
        { Children = Enumerable.Range(0, 256).Select(i => new RespireJobFlow<string>(i.ToString(), "payload")).ToArray() };
        await Assert.That(async () => await queue.EnqueueFlowAsync("flow", large)).Throws<ArgumentException>();
        await Assert.That(async () => await queue.EnqueueFlowAsync("flow", new("root", "payload"), (RespireJobFlowFailurePolicy)42))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await queue.EnqueueFlowAsync("flow", new("root", "payload"), cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        var job = new RespireJob<string>("root", "payload", new("1-0"));
        await Assert.That(async () => await job.GetChildResultsAsync()).Throws<InvalidOperationException>();
        var equal = job with { };
        job.Complete([1]);
        await Assert.That(job.Equals(equal)).IsTrue();
        await Assert.That(job.GetHashCode()).IsEqualTo(equal.GetHashCode());
    }

    [Test]
    public async Task ProgressRequiresAHostedDeliveryAndValidPercentage()
    {
        var job = new RespireJob<string>("id", "payload", new("1-0"));
        await Assert.That(async () => await job.ReportProgressAsync(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await job.ReportProgressAsync(101)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await job.ReportProgressAsync(50)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RetryControlDoesNotChangeJobValueEquality()
    {
        var job = new RespireJob<string>("id", "payload", new("1-0"));
        var equal = job with { };
        var jobs = new HashSet<RespireJob<string>> { job };
        var hash = job.GetHashCode();
        job.RetryAfter(TimeSpan.FromSeconds(1));
        await Assert.That(job.GetHashCode()).IsEqualTo(hash);
        await Assert.That(job.Equals(equal)).IsTrue();
        await Assert.That(jobs.Contains(job)).IsTrue();
        await Assert.That(jobs.Contains(equal)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("{queue}")]
    [Arguments("queue}")]
    public async Task InvalidQueueNameIsRejected(string name)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await Assert.That(() => new RespireJobQueue<string>(client, name, Encoding.UTF8.GetBytes))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(2147483648L)]
    public async Task InvalidWindowIsRejectedBeforeSerialization(long milliseconds)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "queue", _ => throw new InvalidOperationException());
        await Assert.That(async () => await queue.EnqueueAsync("id", "payload", TimeSpan.FromMilliseconds(milliseconds)))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MissingIdAndCanceledEnqueueAreRejectedBeforeSerialization()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "queue", _ => throw new InvalidOperationException());
        await Assert.That(async () => await queue.EnqueueAsync(" ", "payload")).Throws<ArgumentException>();
        await Assert.That(async () => await queue.EnqueueAsync("id", "payload", cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await queue.EnqueueAsync("\ud800", "payload")).Throws<EncoderFallbackException>();
    }

    [Test]
    public async Task NamedQueuesResolveAndDuplicateNamesCannotRegisterAnotherWorker()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client);
        services.AddRespireJobQueue<Handler, string>("first", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString);
        var registered = services.Count;
        await Assert.That(() => services.AddRespireJobQueue<Handler, string>("first", Encoding.UTF8.GetBytes,
            Encoding.UTF8.GetString, new() { TelemetryName = "duplicate" })).Throws<ArgumentException>();
        await Assert.That(services.Count).IsEqualTo(registered);
        services.AddRespireJobQueue<Handler, string>("second", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { TelemetryName = "second" });
        await using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredKeyedService<RespireJobQueue<string>>("first");
        var second = provider.GetRequiredKeyedService<RespireJobQueue<string>>("second");
        await Assert.That(first.StreamKey).IsNotEqualTo(second.StreamKey);
        await Assert.That(first.StreamKey).IsNotEqualTo(first.DeadLetterStreamKey);
    }

    [Test]
    public async Task RegistrationCannotSkipJobsEnqueuedBeforeGroupCreation()
    {
        var services = new ServiceCollection();
        await Assert.That(() => services.AddRespireJobQueue<Handler, string>("queue", Encoding.UTF8.GetBytes,
            Encoding.UTF8.GetString, new() { GroupStart = "$" })).Throws<ArgumentException>();
        await Assert.That(services.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidSchedulingOptionsFailBeforeSerializationOrRegistration()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "queue", _ => throw new InvalidOperationException());
        RespireJobEnqueueOptions[] invalid =
        [
            new() { Delay = TimeSpan.FromTicks(-1) },
            new() { Delay = TimeSpan.FromMilliseconds((long)int.MaxValue + 1) },
            new() { TimeToLive = TimeSpan.Zero },
            new() { DeduplicationWindow = TimeSpan.Zero },
        ];
        foreach (var options in invalid)
            await Assert.That(async () => await queue.EnqueueScheduledAsync("id", "payload", options))
                .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await queue.EnqueueScheduledAsync("id", "payload", new(), new(true)))
            .Throws<OperationCanceledException>();
        var services = new ServiceCollection();
        RespireJobQueueOptions[] policies =
        [
            new() { SchedulingBatchSize = 0 }, new() { SchedulingBatchSize = 257 },
            new() { SchedulingPollInterval = TimeSpan.Zero },
            new() { RetryBackoff = TimeSpan.FromSeconds(2), MaximumRetryBackoff = TimeSpan.FromSeconds(1) },
            new() { RetryBackoff = TimeSpan.FromTicks(-1) },
        ];
        foreach (var policy in policies)
        {
            await Assert.That(() => services.AddRespireJobQueue<Handler, string>("queue", Encoding.UTF8.GetBytes,
                Encoding.UTF8.GetString, null, policy)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(services.Count).IsEqualTo(0);
        }
        var job = new RespireJob<string>("id", "payload", "1-0");
        await Assert.That(() => job.RetryAfter(TimeSpan.FromTicks(-1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(job.RetryAfter(TimeSpan.Zero)).IsEqualTo(RespireStreamWorkerResult.Nack);
    }

    public sealed class Handler : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> message, CancellationToken cancellationToken)
            => ValueTask.FromResult(RespireStreamWorkerResult.Ack);
    }
}
