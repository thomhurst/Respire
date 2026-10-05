using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

// CachedGetManyAsync validates every resolved key before constructing this command.
// Retain that slot through routing and redirects instead of hashing the first key again.
internal readonly struct MGetCommand(RespireValue[] arguments, int? clusterSlot) : IRespCommand
{
    private readonly CmdN _command = new(Verbs.MGet, arguments);
    public ReadCommandKind ReadKind => _command.ReadKind;
    public bool TryGetClusterSlot(out int slot)
    {
        if (clusterSlot is { } validated)
        {
            slot = validated;
            return true;
        }
        return _command.TryGetClusterSlot(out slot);
    }
    public bool TryGetPrimaryKey(out RespireValue key) => _command.TryGetPrimaryKey(out key);
    public bool TryGetArgument(int index, out RespireValue value) => _command.TryGetArgument(index, out value);
    public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
        => _command.TryGetClientCacheKey(operation, out key);
    public void Write(ref RespWriter writer) => _command.Write(ref writer);
}
