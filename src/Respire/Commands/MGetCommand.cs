using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

// CachedGetManyAsync validates every resolved key before constructing this command.
// Retain that slot through routing and redirects instead of hashing the first key again.
internal readonly struct MGetCommand(RespireValue[] arguments, int? clusterSlot) : IRespCommand
{
    // Retain data only. Retaining a command would require IRespCommandWrapper and
    // its explicit admission policy, which also excludes policy-bearing wrappers from hedging.
    // Metadata delegates to CmdN; audit forwarding when IRespCommand gains a member.
    private CmdN Command => new(Verbs.MGet, arguments);
    public ReadCommandKind ReadKind => Verbs.MGet.ReadKind;
    public bool TryGetClusterSlot(out int slot)
    {
        if (clusterSlot is { } validated)
        {
            slot = validated;
            return true;
        }
        return Command.TryGetClusterSlot(out slot);
    }
    public bool TryGetPrimaryKey(out RespireValue key) => Command.TryGetPrimaryKey(out key);
    public bool TryGetArgument(int index, out RespireValue value) => Command.TryGetArgument(index, out value);
    public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
        => Command.TryGetClientCacheKey(operation, out key);
    public void Write(ref RespWriter writer) => Command.Write(ref writer);
}
