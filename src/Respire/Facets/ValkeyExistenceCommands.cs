using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public partial interface IRespireSetCommands
{
    /// <summary>Tests membership, returning null for a missing key. Valkey 9.2+: SISMEMBER XX.</summary>
    ValueTask<bool?> ContainsIfExistsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);
}

internal sealed partial class SetCommands
{
    public ValueTask<bool?> ContainsIfExistsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("SISMEMBER", new Cmd3(Verbs.SIsMember, client.Key(in key), member, "XX"),
            cancellationToken, this, static (SetCommands _, in RespValue reply) => ParseMembership(in reply));

    internal static bool? ParseMembership(in RespValue reply)
        => reply.Type == RespireDataType.Integer ? reply.AsInteger() switch
        {
            -1 => null,
            0 => false,
            1 => true,
            _ => throw new RespireProtocolException("SISMEMBER XX must return -1, 0 or 1."),
        } : throw new RespireProtocolException("SISMEMBER XX must return an integer.");
}

public partial interface IRespireKeyCommands
{
    /// <summary>Moves a key to another database, optionally replacing the destination. REPLACE requires Valkey 9.2+.</summary>
    /// <remarks>Cluster clients are rejected. A false result means no key was moved.</remarks>
    ValueTask<bool> MoveAsync(RespireKey key, int database, bool replace, CancellationToken cancellationToken = default);
}

internal sealed partial class KeyCommands
{
    public ValueTask<bool> MoveAsync(RespireKey key, int database, bool replace, CancellationToken cancellationToken = default)
    {
        ValidateMove(client, database);
        return replace
            ? client.FlagAsync("MOVE", new Cmd3(Verbs.Move, client.Key(in key), database, "REPLACE"), cancellationToken)
            : MoveAsync(key, database, cancellationToken);
    }
}
