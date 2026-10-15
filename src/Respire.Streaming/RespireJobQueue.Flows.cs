using System.Security.Cryptography;

namespace Respire.Streaming;

public sealed partial class RespireJobQueue<T>
{
    /// <summary>Atomically enqueues a tree of at most 256 jobs. Returns false if this flow ID already exists.</summary>
    /// <remarks>Only leaves are initially runnable. All jobs use this queue's payload codec and worker.
    /// Flow IDs are retained until RemoveFlowAsync, including payloads and results; use a new ID for a new run.
    /// The combined UTF-8 job IDs and serialized payloads must not exceed 256 KiB.
    /// Retry an ambiguous enqueue with the same ID. First submission wins. Job IDs are scoped to the flow,
    /// independently of ordinary enqueue deduplication. Reserve the generated key namespace against eviction or edits.</remarks>
    public async ValueTask<bool> EnqueueFlowAsync(string flowId, RespireJobFlow<T> root,
        RespireJobFlowFailurePolicy failurePolicy = RespireJobFlowFailurePolicy.FailParent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!Enum.IsDefined(failurePolicy)) throw new ArgumentOutOfRangeException(nameof(failurePolicy));
        var key = GetFlowKey(flowId);
        JobQueueScheduling.ValidateKeys(_client, [StreamKey, key]);
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = new List<(RespireJobFlow<T> Node, int Parent)> { (root, 0) };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var arguments = new List<RespireValue> { (int)failurePolicy };
        using var trace = new JobEnqueueTrace();
        arguments.Add(trace.Parent);
        arguments.Add(trace.State);
        arguments.Add(key);
        long inputBytes = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var (node, parent) = nodes[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(node.Id);
            inputBytes += StrictUtf8.GetByteCount(node.Id);
            if (!ids.Add(node.Id)) throw new ArgumentException("Flow job IDs must be unique; cycles and shared children are not supported.", nameof(root));
            ArgumentNullException.ThrowIfNull(node.Children);
            if (node.Children.Count > 256 - nodes.Count)
                throw new ArgumentException("A flow can contain at most 256 jobs.", nameof(root));
            foreach (var child in node.Children)
            {
                ArgumentNullException.ThrowIfNull(child);
                nodes.Add((child, i + 1));
            }
            arguments.Add(node.Id);
            var payload = _serialize(node.Payload) ?? throw new InvalidOperationException("The job serializer returned null.");
            inputBytes += payload.Length;
            if (inputBytes > JobQueueFlows.MaximumInputBytes)
                throw new ArgumentException("Flow job IDs and serialized payloads must not exceed 256 KiB in total.", nameof(root));
            arguments.Add(payload);
            arguments.Add(parent);
        }
        var enqueued = await _client.Scripts.ExecuteIntegerAsync(JobQueueFlows.Enqueue, [StreamKey, key],
            [.. arguments], cancellationToken).ConfigureAwait(false);
        trace.Succeeded = true;
        return enqueued == 1;
    }

    /// <summary>Reads all retained node snapshots in tree order, or an empty list for an unknown flow.</summary>
    public ValueTask<IReadOnlyList<RespireJobFlowResult>> GetFlowResultsAsync(string flowId,
        CancellationToken cancellationToken = default)
        => JobQueueFlows.ReadResultsAsync(_client, GetFlowKey(flowId), 0, cancellationToken);

    /// <summary>Deletes a terminal flow's retained payloads, results, and ID reservation. Returns false if absent.</summary>
    /// <remarks>Throws RespireServerException if any job is still active. Queue entries and events are retained
    /// according to their existing retention policy. Reusing a removed flow ID creates a new run.</remarks>
    public async ValueTask<bool> RemoveFlowAsync(string flowId, CancellationToken cancellationToken = default)
        => await _client.Scripts.ExecuteIntegerAsync(JobQueueFlows.Remove, [GetFlowKey(flowId)], [],
            cancellationToken).ConfigureAwait(false) == 1;

    private string GetFlowKey(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        return StreamKey + ":flow:" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(flowId)));
    }
}
