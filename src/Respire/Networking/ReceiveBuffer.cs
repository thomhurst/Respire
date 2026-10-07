using System.Runtime.InteropServices;

namespace Respire.Networking;

/// <summary>Receive-loop storage and its matching release policy; copies do not transfer ownership.</summary>
internal readonly struct ReceiveBuffer
{
    private readonly bool _pinned;

    private ReceiveBuffer(byte[] array, bool pinned)
    {
        Array = array;
        _pinned = pinned;
    }

    public byte[] Array { get; }
    // A pinned array alone is insufficient: ordinary AsMemory still allocates a native
    // GCHandle in Memory.Pin. The pre-pinned flag survives every Memory.Slice operation.
    public Memory<byte> Memory => _pinned
        ? MemoryMarshal.CreateFromPinnedArray(Array, 0, Array.Length)
        : Array.AsMemory();

    public static ReceiveBuffer Rent(int minimumLength, bool pinned)
    {
        return new(pinned
            ? GC.AllocateUninitializedArray<byte>(minimumLength, pinned: true)
            : RespirePools.ResponsePayloads.Rent(minimumLength), pinned);
    }

    public ReceiveBuffer Grow(int minimumLength) => Rent(minimumLength, _pinned);

    public void Return()
    {
        // Only the receive loop releases storage, after the last read using it has ended.
        // GC-owned pinned arrays must never enter the unpinned response-payload pool.
        if (!_pinned) RespirePools.ResponsePayloads.Return(Array);
    }
}
