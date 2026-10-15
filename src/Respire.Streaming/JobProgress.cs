namespace Respire.Streaming;

internal interface IJobDelivery
{
    void Attach(JobProgress progress);
    byte[]? Result { get; }
}

internal sealed class JobProgress(IRespireClient client, string stream, string events, string group,
    string consumer, RespireStreamId entryId, long attempt, string? flowKey, int node)
{
    internal ValueTask<IReadOnlyList<RespireJobFlowResult>> ReadChildrenAsync(CancellationToken token)
        => flowKey is null
            ? ValueTask.FromResult<IReadOnlyList<RespireJobFlowResult>>([])
            : JobQueueFlows.ReadResultsAsync(client, flowKey, node, token);

    internal async ValueTask<bool> ReportAsync(int percent, CancellationToken token)
        => await client.Scripts.ExecuteIntegerAsync(JobQueueScripts.Progress, [stream, events],
            [group, consumer, entryId.Value, attempt, percent], token).ConfigureAwait(false) == 1;
}
