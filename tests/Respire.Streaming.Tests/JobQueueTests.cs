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
    public async Task ProgressRequiresAHostedDeliveryAndValidPercentage()
    {
        var job = new RespireJob<string>("id", "payload", new("1-0"));
        await Assert.That(async () => await job.ReportProgressAsync(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await job.ReportProgressAsync(101)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await job.ReportProgressAsync(50)).Throws<InvalidOperationException>();
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
