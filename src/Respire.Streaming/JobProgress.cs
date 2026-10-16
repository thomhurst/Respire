using System.Text;
using Respire.Internal;

namespace Respire.Streaming;

internal interface IJobDelivery
{
    string Id { get; }
    void Attach(JobProgress progress);
    byte[]? Result { get; }
}

internal sealed class JobProgress(IRespireClient client, string stream, string events, string group,
    string consumer, RespireStreamId entryId, long attempt, string? flowKey, int node,
    string jobId, RespireJobQueueOptions? options)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly RespireJobQueueOptions Defaults = new();

    internal async ValueTask<bool> LogAsync(string message, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(message);
        var policy = options ?? Defaults;
        if (StrictUtf8.GetByteCount(message) > policy.MaximumLogLineBytes)
            throw new ArgumentOutOfRangeException(nameof(message), "The log line exceeds MaximumLogLineBytes.");
        token.ThrowIfCancellationRequested();
        var key = JobLogKeys.ForJob(stream, jobId);
        JobQueueScheduling.ValidateKeys(client, [stream, key]);
        return await client.Scripts.ExecuteIntegerAsync(JobQueueScripts.Log, [stream, key],
            [group, consumer, entryId.Value, attempt, message, policy.MaximumLogLines,
                (long)Math.Ceiling(policy.LogRetention.TotalMilliseconds)], token).ConfigureAwait(false) == 1;
    }

    internal ValueTask<IReadOnlyList<RespireJobFlowResult>> ReadChildrenAsync(CancellationToken token)
        => flowKey is null
            ? ValueTask.FromResult<IReadOnlyList<RespireJobFlowResult>>([])
            : JobQueueFlows.ReadResultsAsync(client, flowKey, node, token);

    internal async ValueTask<bool> ReportAsync(int percent, CancellationToken token)
        => await client.Scripts.ExecuteIntegerAsync(JobQueueScripts.Progress, [stream, events],
            [group, consumer, entryId.Value, attempt, percent], token).ConfigureAwait(false) == 1;
}
