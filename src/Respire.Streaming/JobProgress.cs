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
    string jobId, RespireJobQueueOptions? options, bool groupLease = false, string? checkpointKey = null)
{
    private static readonly RespireScript LeasedProgress = JobGroupControls.Fence(JobQueueScripts.Progress);
    private static readonly RespireScript LeasedLog = JobGroupControls.Fence(JobQueueScripts.Log);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly RespireJobQueueOptions Defaults = new();

    internal ValueTask<RespireJobCheckpoint?> ReadCheckpointAsync(CancellationToken token)
        => JobCheckpoints.ReadAsync(client, checkpointKey
            ?? throw new InvalidOperationException("Enable MaximumRuntime when enqueueing a resumable job."), token);

    internal async ValueTask<bool> SaveCheckpointAsync(string cursor, byte[] state, int? percent, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(state);
        if (StrictUtf8.GetByteCount(cursor) > 1024) throw new ArgumentOutOfRangeException(nameof(cursor));
        if (state.Length > 4096) throw new ArgumentOutOfRangeException(nameof(state));
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        var key = checkpointKey ?? throw new InvalidOperationException("Enable MaximumRuntime when enqueueing a resumable job.");
        JobQueueScheduling.ValidateKeys(client, [stream, key]);
        return await client.Scripts.ExecuteIntegerAsync(groupLease ? JobCheckpoints.LeasedSave : JobCheckpoints.Save,
            groupLease ? [stream, key, stream + ":group-leases", stream + ":group-owners"] : [stream, key],
            [group, consumer, entryId.Value, attempt, cursor, state, percent ?? -1], token).ConfigureAwait(false) == 1;
    }

    internal async ValueTask<bool> LogAsync(string message, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(message);
        var policy = options ?? Defaults;
        if (StrictUtf8.GetByteCount(message) > policy.MaximumLogLineBytes)
            throw new ArgumentOutOfRangeException(nameof(message), "The log line exceeds MaximumLogLineBytes.");
        token.ThrowIfCancellationRequested();
        var key = JobLogKeys.ForJob(stream, jobId);
        JobQueueScheduling.ValidateKeys(client, [stream, key]);
        return await client.Scripts.ExecuteIntegerAsync(groupLease ? LeasedLog : JobQueueScripts.Log,
            groupLease ? [stream, key, stream + ":group-leases", stream + ":group-owners"] : [stream, key],
            [group, consumer, entryId.Value, attempt, message, policy.MaximumLogLines,
                (long)Math.Ceiling(policy.LogRetention.TotalMilliseconds)], token).ConfigureAwait(false) == 1;
    }

    internal ValueTask<IReadOnlyList<RespireJobFlowResult>> ReadChildrenAsync(CancellationToken token)
        => flowKey is null
            ? ValueTask.FromResult<IReadOnlyList<RespireJobFlowResult>>([])
            : JobQueueFlows.ReadResultsAsync(client, flowKey, node, token);

    internal async ValueTask<bool> ReportAsync(int percent, CancellationToken token)
        => await client.Scripts.ExecuteIntegerAsync(groupLease ? LeasedProgress : JobQueueScripts.Progress,
            groupLease ? [stream, events, stream + ":group-leases", stream + ":group-owners"] : [stream, events],
            [group, consumer, entryId.Value, attempt, percent], token).ConfigureAwait(false) == 1;
}
