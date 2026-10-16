using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class JobLogTests
{
    [Test]
    public async Task LogsRequireHostedDelivery()
    {
        var job = new RespireJob<string>("id", "payload", "1-0");
        await Assert.That(async () => await job.LogAsync("message")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task LogLimitsAreValidatedAtRegistration()
    {
        foreach (var options in new RespireJobQueueOptions[]
        {
            new() { MaximumLogLines = 0 }, new() { MaximumLogLines = 1001 },
            new() { MaximumLogLineBytes = 0 }, new() { MaximumLogLineBytes = 16385 },
            new() { LogRetention = TimeSpan.Zero }, new() { LogRetention = TimeSpan.FromDays(30) },
        })
            await Assert.That(() => new ServiceCollection().AddRespireJobQueue<Handler, string>(
                "logs", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, null, options))
                .Throws<ArgumentOutOfRangeException>();
    }

    public sealed class Handler : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
            => ValueTask.FromResult(RespireStreamWorkerResult.Ack);
    }
}
