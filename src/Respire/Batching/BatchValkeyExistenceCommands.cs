using Respire.Commands;
using Respire.Internal;

namespace Respire;

public partial interface IRespireBatchSetCommands
{
    /// <summary>Queues SISMEMBER XX; null means a missing key. Requires Valkey 9.2+.</summary>
    RespirePending<bool?> ContainsIfExists(RespireKey key, RespireValue member);
}

internal sealed partial class BatchSetCommands
{
    public RespirePending<bool?> ContainsIfExists(RespireKey key, RespireValue member)
        => sink.Add<Cmd3, bool?>("SISMEMBER", new(Verbs.SIsMember, sink.Client.Key(in key), member, "XX"),
            static (_, reply) => SetCommands.ParseMembership(in reply));
}

public partial interface IRespireBatchKeyCommands
{
    /// <summary>Queues MOVE, optionally replacing the destination on Valkey 9.2+. Cluster clients are rejected.</summary>
    RespirePending<bool> Move(RespireKey key, int database, bool replace);
}

internal sealed partial class BatchKeyCommands
{
    public RespirePending<bool> Move(RespireKey key, int database, bool replace)
    {
        KeyCommands.ValidateMove(sink.Client, database);
        return replace
            ? sink.Add<Cmd3, bool>("MOVE", new(Verbs.Move, sink.Client.Key(in key), database, "REPLACE"),
                static (_, reply) => ResponseReader.Flag(in reply))
            : Move(key, database);
    }
}
