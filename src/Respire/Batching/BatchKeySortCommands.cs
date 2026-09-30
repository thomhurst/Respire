using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

public partial interface IBatchKeyCommands
{
    /// <summary>Queues SORT/SORT_RO, preserving null external GET values.</summary>
    RespirePending<string?[]> Sort(RespireKey key, RespireSortOptions? options = null);
    /// <summary>Queues SORT/SORT_RO and deserializes members. Use byte[] for owned binary results.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?[]> Sort<T>(RespireKey key, RespireSortOptions? options = null);
    /// <summary>Queues SORT STORE; source and destination must share a Cluster slot.</summary>
    RespirePending<long> SortStore(RespireKey key, RespireKey destination, RespireSortOptions? options = null);
    /// <summary>Queues RANDOMKEY; requires an unprefixed, non-Cluster client.</summary>
    RespirePending<RespireKey?> Random();
    /// <summary>Queues MOVE to another database; Cluster clients are rejected.</summary>
    RespirePending<bool> Move(RespireKey key, int database);
}

internal sealed partial class BatchKeyCommands
{
    public RespirePending<string?[]> Sort(RespireKey key, RespireSortOptions? options = null)
    {
        var (operation, command) = KeyCommands.SortCommand(sink.Client, key, options);
        return sink.Add<CmdN, string?[]>(operation, command,
            static (_, reply) => ResponseReader.NullableStringArray(in reply));
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?[]> Sort<T>(RespireKey key, RespireSortOptions? options = null)
    {
        var (operation, command) = KeyCommands.SortCommand(sink.Client, key, options);
        return sink.Add<CmdN, T?[]>(operation, command,
            static (c, reply) => c.DeserializeNullableArray<T>(in reply));
    }

    public RespirePending<long> SortStore(RespireKey key, RespireKey destination, RespireSortOptions? options = null)
    {
        var (operation, command) = KeyCommands.SortCommand(sink.Client, key, options, destination);
        return sink.Add<CmdN, long>(operation, command, key, destination,
            static (_, reply) => ResponseReader.Integer(in reply));
    }

    public RespirePending<RespireKey?> Random()
    {
        KeyCommands.ValidateRandom(sink.Client);
        return sink.Add<Cmd, RespireKey?>("RANDOMKEY", new Cmd(Verbs.RandomKey),
            static (_, reply) => KeyCommands.ParseRandom(in reply));
    }

    public RespirePending<bool> Move(RespireKey key, int database)
    {
        KeyCommands.ValidateMove(sink.Client, database);
        return sink.Add<Cmd2, bool>("MOVE", new Cmd2(Verbs.Move, sink.Client.Key(in key), database),
            static (_, reply) => ResponseReader.Flag(in reply));
    }
}
