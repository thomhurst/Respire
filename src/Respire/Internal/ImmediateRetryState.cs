using Reservoir;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

// One logical send owns this rental until every awaited attempt ends. Transports copy the
// frame synchronously before publishing a reply slot; FIFO retirement owns those copies.
// Write evidence remains attempt-local and unpooled, independent of this reusable owner.
internal sealed class ImmediateRetryState
{
    private static readonly ObjectPool<ImmediateRetryState, PoolPolicy> Pool = new(256);
    private readonly WriteBuffer _buffer = new(256);
    private int? _slot;

    internal RespireCommandRetryCategory Category { get; private set; }
    internal ReadOnlyMemory<byte> Frame => _buffer.WrittenMemory;
    internal bool IsRetry;
    internal bool AcceptedAttempt;
    internal int Retries;

    internal static ImmediateRetryState Rent<TCommand>(in TCommand command) where TCommand : struct, IRespCommand
    {
        var state = Pool.Rent();
        try
        {
            state.Category = command.RetryCategory;
            state._slot = command.TryGetClusterSlot(out var slot) ? slot : null;
            var writer = new RespWriter(state._buffer, command.GetWriteSizeHint());
            command.Write(ref writer);
            writer.Complete();
            return state;
        }
        catch
        {
            state.Return();
            throw;
        }
    }

    internal bool TryGetClusterSlot(out int slot)
    {
        slot = _slot.GetValueOrDefault();
        return _slot.HasValue;
    }

    internal void Accepted()
    {
        AcceptedAttempt = true;
        if (IsRetry) Retries++;
    }

    internal void Return()
    {
        // Reset also abandons unfinished serialization. Clear the whole rental, including
        // uncommitted bytes, and return its array instead of retaining large command payloads.
        _buffer.Reset();
        _buffer.GetSpan(_buffer.Capacity).Clear();
        _buffer.Release();
        Category = default;
        _slot = null;
        IsRetry = false;
        AcceptedAttempt = false;
        Retries = 0;
        Pool.Return(this);
    }

    private readonly struct PoolPolicy : IPooledObjectPolicy<ImmediateRetryState>
    {
        public ImmediateRetryState Create() => new();
        public bool TryReset(ImmediateRetryState state) => true;
    }
}
