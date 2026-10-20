using System.Diagnostics;
using System.Security.Cryptography;

namespace Respire.Streaming;

public sealed partial class RespireJobQueue<T>
{
    /// <summary>Queues an immediate job in the caller's transaction, returning a result readable after commit.</summary>
    /// <remarks>
    /// Create the transaction from the same client instance (including its key prefix) as this queue.
    /// Serialization happens now; enqueue and deduplication happen only inside EXEC. Disposal without commit
    /// and WATCH aborts create no job. All transaction keys must share a Cluster slot.
    /// Requires the same Redis 7.0+ Lua permissions as EnqueueAsync. Redis runtime command errors do not
    /// roll back other commands: inspect every pending result after commit. Retry ambiguous outcomes with
    /// the same job ID and idempotent business writes. A batch cannot provide this atomicity.
    /// </remarks>
    public RespirePending<RespireJobEnqueueResult> Enqueue(RespireTransactionBase transaction,
        string jobId, T payload, TimeSpan? deduplicationWindow = null)
        => Enqueue(transaction, RespireJobDeduplicationMode.FirstSubmissionWins, jobId, payload, deduplicationWindow);

    /// <summary>Queues an immediate job with an explicit deduplication mode, applied atomically inside EXEC.</summary>
    /// <remarks>The first successful enqueue fixes the mode and window, as for EnqueueAsync.</remarks>
    public RespirePending<RespireJobEnqueueResult> Enqueue(RespireTransactionBase transaction,
        RespireJobDeduplicationMode deduplicationMode, string jobId, T payload, TimeSpan? deduplicationWindow = null)
    {
        var sink = GetTransactionSink(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        JobDeduplication.Validate(deduplicationMode);
        var window = deduplicationWindow ?? TimeSpan.FromDays(1);
        JobQueueScheduling.ValidateDuration(window, nameof(deduplicationWindow));
        var deduplicationKey = GetTransactionDeduplicationKey(jobId);
        JobQueueScheduling.ValidateKeys(_client, [StreamKey, deduplicationKey]);
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        var (parent, state) = CaptureTransactionTrace();
        return QueueEnqueue(sink, EnqueueScript, [StreamKey, deduplicationKey],
            [jobId, bytes, (long)Math.Ceiling(window.TotalMilliseconds), parent, state, (int)deduplicationMode]);
    }

    /// <summary>Queues a delayed or prioritized job in the caller's transaction.</summary>
    /// <remarks>
    /// Uses the same transaction, deduplication and failure contract as Enqueue. Delay, lifetime and
    /// deduplication windows start at execution on the Redis server, not when this method is called.
    /// The result identifies the staged entry; a scheduling-enabled worker promotes it to the ready stream.
    /// </remarks>
    public RespirePending<RespireJobEnqueueResult> EnqueueScheduled(RespireTransactionBase transaction,
        string jobId, T payload, RespireJobEnqueueOptions options)
    {
        var sink = GetTransactionSink(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var deduplicationKey = GetTransactionDeduplicationKey(jobId);
        string[] keys = options.Group is null
            ? [.. JobQueueScheduling.Keys(StreamKey), deduplicationKey]
            : [.. JobQueueScheduling.Keys(StreamKey), deduplicationKey, .. JobGroupScheduling.Keys(StreamKey).Skip(3)];
        JobQueueScheduling.ValidateKeys(_client, keys);
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        var (parent, state) = CaptureTransactionTrace();
        return QueueEnqueue(sink, JobQueueScheduling.Enqueue, [.. keys],
            [jobId, bytes, (long)Math.Ceiling(options.DeduplicationWindow.TotalMilliseconds),
                (long)Math.Ceiling(options.Delay.TotalMilliseconds), options.Priority,
                (long)Math.Ceiling(options.TimeToLive?.TotalMilliseconds ?? 0), parent, state,
                options.Group ?? string.Empty, (long)Math.Ceiling(options.MaximumRuntime?.TotalMilliseconds ?? 0),
                options.MaximumRuntime is null ? string.Empty : Guid.NewGuid().ToString("N"),
                (int)options.DeduplicationMode]);
    }

    private IPendingSink GetTransactionSink(RespireTransactionBase transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var sink = (IPendingSink)transaction;
        if (!UsesTransactionClient(sink.Client))
            throw new ArgumentException("Create the transaction from the same client instance as the queue.", nameof(transaction));
        return sink;
    }

    private bool UsesTransactionClient(RespireClient client)
    {
        if (_client is RespireClient direct)
            return ReferenceEquals(client, direct);

        // A decorator's transaction factory exposes the concrete owner, including its key-prefix
        // view. Do not compare endpoints: separate clients and views must remain distinct.
        var probe = _client.CreateTransaction();
        try
        {
            return ReferenceEquals(client, ((IPendingSink)probe).Client);
        }
        finally
        {
            // RespireTransaction is sealed and has no WATCH connection. Discarding this empty
            // transaction completes synchronously and only releases its local buffer.
            probe.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    private string GetTransactionDeduplicationKey(string jobId)
        => StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(jobId)));

    private static (string Parent, string State) CaptureTransactionTrace()
    {
        // Propagate the caller's context without reporting a successful enqueue before EXEC.
        var activity = Activity.Current;
        return activity?.IdFormat == ActivityIdFormat.W3C
            ? (activity.Id ?? string.Empty, activity.TraceStateString ?? string.Empty)
            : (string.Empty, string.Empty);
    }

    private static RespirePending<RespireJobEnqueueResult> QueueEnqueue(IPendingSink sink,
        RespireScript script, RespireKey[] keys, RespireValue[] args)
    {
        // EVAL carries the script source: a transaction cannot recover from NOSCRIPT after EXEC.
        var command = new BatchScriptCommand(script.EvalVerb, script.Source,
            sink.Client.BuildScriptTail(keys, args), keys.Length, script.RetryCategory);
        return sink.Add<BatchScriptCommand, RespireJobEnqueueResult>(script.EvalOperation, command, keys,
            static (_, value) =>
            {
                // Read the borrowed EXEC reply directly; only the entry ID must outlive it.
                var result = value.AsArray();
                return new(new(result[0].AsString()), result[1].AsInteger() == 1);
            });
    }
}
