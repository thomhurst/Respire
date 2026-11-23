using System.Text.Json.Serialization;
using Respire;
using Respire.Streaming;

namespace RespireWorker;

public sealed record SampleJob(string Message);

[JsonSerializable(typeof(SampleJob))]
internal partial class JobJson : JsonSerializerContext
{
}

public sealed class SampleJobHandler(IRespireClient redis, ILogger<SampleJobHandler> logger)
    : IRespireStreamHandler<RespireJob<SampleJob>>
{
    public async ValueTask<RespireStreamWorkerResult> HandleAsync(
        RespireJob<SampleJob> job, CancellationToken cancellationToken)
    {
        // Deterministic overwrite tolerates redelivery. Real side effects also need idempotency.
        await redis.SetAsync(SampleJobKeys.GetResultKey(job.Id), job.Payload.Message.ToUpperInvariant(),
            expiry: TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        logger.LogInformation("Processed job {JobId}", job.Id);
        return RespireStreamWorkerResult.Ack;
    }
}

internal static class SampleJobKeys
{
    // Escape braces before putting the assembly name inside a Redis Cluster hash tag.
    internal static string QueueName
    {
        get;
    } = Uri.EscapeDataString(typeof(SampleJobKeys).Assembly.GetName().Name!);

    internal static string GetResultKey(string id)
    {
        return $"{QueueName}:result:{id}";
    }
}
