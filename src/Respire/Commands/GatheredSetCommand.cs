using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Reservoir;
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
    public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);
    public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);
    public void ValidateAdmission() { }
    public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;
    public void OnAccepted() { }
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
        RespireValue value, CancellationToken cancellationToken)
        => value.TryGetByteMemory(out var memory) && memory.Length >= 8 * 1024
            && MemoryMarshal.TryGetArray(memory, out var payload)
            ? SendBorrowedAsync(client, command, payload, cancellationToken)
            : client.OkOrNullAsync("SET", command, cancellationToken);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<bool> SendBorrowedAsync(RespireClient client, SetCommand command,
        ArraySegment<byte> payload, CancellationToken cancellationToken)
    {
        var lease = GatheredSetWriteLease.Rent();
        try
        {
            return await client.OkOrNullAsync("SET", new GatheredSetCommand(command, payload, lease), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Response cancellation, deadlines and connection failure can precede socket completion.
            // Only the persistent sender releases an in-flight write reference; queued writes are
            // released by abort before they can reach the socket. No caller memory is reused early.
            await lease.FinishOperation().ConfigureAwait(false);
        }
    }
}

/// <summary>Bounded pooled ownership barrier shared by redirect attempts of one public SET.</summary>
internal sealed class GatheredSetWriteLease : IValueTaskSource<bool>
{
    private static readonly ObjectPool<GatheredSetWriteLease, PoolPolicy> Pool = new(4096);
    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
    private int _references;

    internal static GatheredSetWriteLease Rent()
    {
        var lease = Pool.Rent();
        lease._references = 1;
        return lease;
    }

    internal void RetainWrite() => Interlocked.Increment(ref _references);

    internal void ReleaseWrite()
    {
        if (Interlocked.Decrement(ref _references) == 0) _core.SetResult(true);
    }

    internal ValueTask<bool> FinishOperation()
    {
        var pending = new ValueTask<bool>(this, _core.Version);
        ReleaseWrite();
        return pending;
    }

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        var result = _core.GetResult(token);
        Pool.Return(this);
        return result;
    }
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

    private readonly struct PoolPolicy : IPooledObjectPolicy<GatheredSetWriteLease>
    {
        public GatheredSetWriteLease Create() => new();
        public bool TryReset(GatheredSetWriteLease lease) { lease._core.Reset(); return true; }
    }
}
