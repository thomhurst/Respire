using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Retains the slot validated by CachedGetManyAsync through routing and redirects.</summary>
internal readonly struct MGetCommand(RespireValue[] arguments, int? clusterSlot) : IRespCommand
{
    // Retain data only. Retaining a command would require IRespCommandWrapper and
    // its explicit admission policy, which also excludes policy-bearing wrappers from hedging.
    // Metadata delegates to CmdN; audit forwarding when IRespCommand gains a member.
    /// <summary>Builds transient command metadata without retaining an execution policy.</summary>
    private CmdN Command => new(Verbs.MGet, arguments);
    /// <inheritdoc />
    public ReadCommandKind ReadKind => Verbs.MGet.ReadKind;
    public int GetWriteSizeHint() => Command.GetWriteSizeHint();
    /// <summary>Uses the validated slot, falling back to normal command hashing when none was supplied.</summary>
    public bool TryGetClusterSlot(out int slot)
    {
        if (clusterSlot is { } validated)
        {
            slot = validated;
            return true;
        }
        return Command.TryGetClusterSlot(out slot);
    }
    /// <inheritdoc />
    public bool TryGetPrimaryKey(out RespireValue key) => Command.TryGetPrimaryKey(out key);
    /// <inheritdoc />
    public bool TryGetArgument(int index, out RespireValue value) => Command.TryGetArgument(index, out value);
    /// <inheritdoc />
    public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
        => Command.TryGetClientCacheKey(operation, out key);
    /// <inheritdoc />
    public void Write(ref RespWriter writer) => Command.Write(ref writer);
}
