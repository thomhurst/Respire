using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Reservoir;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>A public SET whose completion retains caller memory until all accepted writes finish.</summary>
internal readonly struct GatheredSetCommand(SetCommand command, ArraySegment<byte> payload,
    GatheredSetWriteLease lease) : IRespCommandWrapper
{
    internal ArraySegment<byte> Payload => payload;
    internal GatheredSetWriteLease Lease => lease;
    public int GetWriteSizeHint() => command.GetWriteSizeHint();
    public ReadCommandKind ReadKind => command.ReadKind;
    public bool IsConnectionProtocol => false;
    public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);
    public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);
    public void ValidateAdmission() { }
    public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;
    public void OnAccepted() { }
    public ClientSideCacheCoordinator.MutationFence GetMutationFence() => default;
    public void Write(ref RespWriter writer) => command.Write(ref writer);

    internal int WriteEnvelope(WriteBuffer buffer)
    {
        var writer = new RespWriter(buffer, checked(command.GetWriteSizeHint() - payload.Count));
        command.WriteGatherHeader(ref writer, payload.Count);
        writer.Complete();
        var offset = buffer.Count;
        writer = new RespWriter(buffer, 2 + CommandWriteSizeHint.SetOptions);
        writer.WriteBulkStringTerminator();
        command.WriteOptions(ref writer);
        writer.Complete();
        return offset;
    }

    internal static ValueTask<bool> SendAsync(RespireClient client, SetCommand command,
        RespireValue value, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
        => value.TryGetByteMemory(out var memory) && memory.Length >= 8 * 1024
            && MemoryMarshal.TryGetArray(memory, out var payload)
            ? SendBorrowedAsync(client, command, payload, cancellationToken, observation)
            : client.OkOrNullAsync("SET", command, cancellationToken, observation);

    private static ValueTask<bool> SendBorrowedAsync(RespireClient client, SetCommand command,
        ArraySegment<byte> payload, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var lease = GatheredSetWriteLease.Rent();
        ValueTask<bool> response;
        try
        {
            response = client.OkOrNullAsync("SET", new GatheredSetCommand(command, payload, lease), cancellationToken, observation);
        }
        catch (Exception error)
        {
            response = ValueTask.FromException<bool>(error);
        }
        return lease.CompleteResponseAsync(response);
    }
}

/// <summary>Bounded pooled ownership barrier shared by redirect attempts of one public SET.</summary>
internal sealed class GatheredSetWriteLease : IValueTaskSource<bool>, IThreadPoolWorkItem
{
    private static readonly ObjectPool<GatheredSetWriteLease, PoolPolicy> Pool = new(4096);
    private ManualResetValueTaskSourceCore<bool> _core;
    private int _references;
    private readonly Action _responseContinuation;
    private ValueTask<bool> _response;
    private ExecutionContext? _responseContext;
    private bool _result = true;
    private Exception? _error;
    private int _responseConsumed;
    private bool _reusable = true;

    private GatheredSetWriteLease() => _responseContinuation = CompleteResponse;

    internal static GatheredSetWriteLease Rent()
    {
        var lease = Pool.Rent();
        lease._references = 1;
        return lease;
    }

    internal void RetainWrite() => Interlocked.Increment(ref _references);

    internal void ReleaseWrite()
    {
        // The persistent sender must never run a public continuation. Queue the lease
        // itself instead of letting the value-task core allocate a dispatch callback.
        if (Interlocked.Decrement(ref _references) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    void IThreadPoolWorkItem.Execute() => PublishResponse();

    private void PublishResponse()
    {
        if (_error is { } error) _core.SetException(error);
        else _core.SetResult(_result);
    }

    internal ValueTask<bool> CompleteResponseAsync(ValueTask<bool> response)
    {
        // The bounded lease is also the public source. A separate pooled async frame
        // still allocates when concurrent responses exhaust the builder's small cache.
        var pending = new ValueTask<bool>(this, _core.Version);
        _response = response;
        try
        {
            if (response.IsCompleted) ConsumeResponse();
            else
            {
                _responseContext = ExecutionContext.Capture();
                response.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_responseContinuation);
            }
        }
        catch (Exception error)
        {
            // Registration may throw after retaining a callback. Never reuse this lease:
            // a late callback must not observe a different public operation's state.
            _reusable = false;
            ConsumeResponse(error);
        }
        return pending;
    }

    private void CompleteResponse()
    {
        if (_responseContext is { } context)
            ExecutionContext.Run(context, static state => ((GatheredSetWriteLease)state!).ConsumeResponse(), this);
        else ConsumeResponse();
    }

    private void ConsumeResponse(Exception? registrationError = null)
    {
        if (Interlocked.Exchange(ref _responseConsumed, 1) != 0) return;
        try
        {
            if (registrationError is not null) _error = registrationError;
            else _result = _response.GetAwaiter().GetResult();
        }
        catch (Exception error) { _error = error; }
        // Clear response ownership before the last reference can publish and recycle us.
        _response = default;
        _responseContext = null;
        // Cancellation, deadlines and connection failure can precede kernel completion.
        // The operation reference ends only after consuming its response exactly once.
        if (Interlocked.Decrement(ref _references) != 0) return;
        // Preserve the response's existing completion dispatch when all writes finished.
        // Only a final write release needs a separate worker to leave the sender thread.
        PublishResponse();
    }

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        if (_core.GetStatus(token) == ValueTaskSourceStatus.Pending)
            throw new InvalidOperationException("The gathered SET response has not completed.");
        try { return _core.GetResult(token); }
        finally { if (_reusable) Pool.Return(this); }
    }
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

    private readonly struct PoolPolicy : IPooledObjectPolicy<GatheredSetWriteLease>
    {
        public GatheredSetWriteLease Create() => new();
        public bool TryReset(GatheredSetWriteLease lease)
        {
            lease._error = null;
            lease._result = true;
            lease._responseConsumed = 0;
            lease._core.Reset();
            return true;
        }
    }
}
