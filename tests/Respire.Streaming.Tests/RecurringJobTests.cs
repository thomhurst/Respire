using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class RecurringJobTests
{
    [Test]
    public async Task InvalidSchedulesFailBeforeSerialization()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var scheduler = new RespireJobQueue<string>(client, "queue", _ => throw new InvalidOperationException()).CreateScheduler();
        await Assert.That(async () => await scheduler.UpsertAsync("id", "payload", new())).Throws<ArgumentException>();
        await Assert.That(async () => await scheduler.UpsertAsync("id", "payload", new()
        {
            Interval = TimeSpan.FromSeconds(1), Cron = "* * * * *",
        })).Throws<ArgumentException>();
        foreach (var interval in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromTicks(1) })
            await Assert.That(async () => await scheduler.UpsertAsync("id", "payload", new() { Interval = interval }))
                .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await scheduler.UpsertAsync("id", "payload", new()
        {
            Interval = TimeSpan.FromSeconds(1), MissedTickPolicy = (RespireJobMissedTickPolicy)123,
        })).Throws<ArgumentOutOfRangeException>();
        foreach (var cron in new[] { "", "not cron", "60 * * * *", "* * * * * *" })
            await Assert.That(async () => await scheduler.UpsertAsync("id", "payload", new() { Cron = cron }))
                .Throws<Cronos.CronFormatException>();
    }

    [Test]
    public async Task InvalidIdsPassBoundsAndCancellationAreRejected()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var scheduler = new RespireJobQueue<string>(client, "queue", _ => throw new InvalidOperationException()).CreateScheduler();
        var schedule = new RespireJobSchedule { Interval = TimeSpan.FromSeconds(1) };
        await Assert.That(async () => await scheduler.UpsertAsync(" ", "payload", schedule)).Throws<ArgumentException>();
        await Assert.That(async () => await scheduler.RemoveAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await scheduler.UpsertAsync("\ud800", "payload", schedule)).Throws<EncoderFallbackException>();
        await Assert.That(async () => await scheduler.RemoveAsync("\ud800")).Throws<EncoderFallbackException>();
        await Assert.That(async () => await scheduler.UpsertAsync("id", "payload", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await scheduler.UpsertAsync("id", "payload", schedule, new(true))).Throws<OperationCanceledException>();
        await Assert.That(async () => await scheduler.RemoveAsync("id", new(true))).Throws<OperationCanceledException>();
        await Assert.That(async () => await scheduler.RunDueAsync(cancellationToken: new(true))).Throws<OperationCanceledException>();
        await Assert.That(async () => await scheduler.RunDueAsync(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await scheduler.RunDueAsync(257)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RegistrationRequiresMatchingQueueAndRejectsDuplicatesWithoutMutation()
    {
        var services = new ServiceCollection();
        await Assert.That(() => services.AddRespireJobScheduler<string>("queue")).Throws<ArgumentException>();
        await Assert.That(services.Count).IsEqualTo(0);
        services.AddRespireJobQueue<Handler, string>("queue", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString);
        var registered = services.Count;
        await Assert.That(() => services.AddRespireJobScheduler<int>("queue")).Throws<ArgumentException>();
        await Assert.That(() => services.AddRespireJobScheduler<string>("missing")).Throws<ArgumentException>();
        await Assert.That(() => services.AddRespireJobScheduler<string>("queue", TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => services.AddRespireJobScheduler<string>("queue", maximumTicksPerPass: 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(services.Count).IsEqualTo(registered);
        services.AddRespireJobScheduler<string>("queue");
        registered = services.Count;
        await Assert.That(() => services.AddRespireJobScheduler<string>("queue")).Throws<ArgumentException>();
        await Assert.That(services.Count).IsEqualTo(registered);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistrationResolvesNamedSchedulersAndHostedServices(bool logging)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client);
        if (logging) services.AddLogging();
        services.AddRespireJobQueue<Handler, string>("first", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { TelemetryName = "first" });
        services.AddRespireJobQueue<Handler, string>("second", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { TelemetryName = "second" });
        services.AddRespireJobScheduler<string>("first");
        services.AddRespireJobScheduler<string>("second");
        await using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredKeyedService<RespireJobScheduler<string>>("first");
        var second = provider.GetRequiredKeyedService<RespireJobScheduler<string>>("second");
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        await Assert.That(ReferenceEquals(first, provider.GetRequiredKeyedService<RespireJobScheduler<string>>("first"))).IsTrue();
        await Assert.That(provider.GetServices<IHostedService>().Count()).IsEqualTo(4);
    }

    public sealed class Handler : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> message, CancellationToken cancellationToken)
            => ValueTask.FromResult(RespireStreamWorkerResult.Ack);
    }
}
