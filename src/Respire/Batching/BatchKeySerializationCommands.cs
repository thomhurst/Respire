using Respire.Commands;
using Respire.Internal;

namespace Respire;

public partial interface IBatchKeyCommands
{
    /// <summary>An owned Redis-serialized payload, or null for a missing key. Does not include expiry. Redis: DUMP.</summary>
    RespirePending<byte[]?> Dump(RespireKey key);

    /// <summary>Restores a Redis-serialized payload; true on OK. Redis: RESTORE.</summary>
    /// <remarks>Uses the expiry and option rules of IKeyCommands.RestoreAsync. The payload is borrowed:
    /// do not modify its bytes until batch/transaction execution completes.</remarks>
    RespirePending<bool> Restore(RespireKey key, ReadOnlyMemory<byte> payload,
        RespireExpiry expiry = default, RespireRestoreOptions options = default);
}

internal sealed partial class BatchKeyCommands
{
    public RespirePending<byte[]?> Dump(RespireKey key)
        => sink.Add<Cmd1, byte[]?>("DUMP", new Cmd1(RespireCommands.Key.DUMP.Verb, sink.Client.Key(in key)),
            static (_, reply) => ResponseReader.BytesOrNull(in reply));

    public RespirePending<bool> Restore(RespireKey key, ReadOnlyMemory<byte> payload,
        RespireExpiry expiry = default, RespireRestoreOptions options = default)
        => sink.Add<CmdN, bool>("RESTORE", KeyCommands.RestoreCommand(sink.Client, key, payload, expiry, options),
            static (_, reply) => ResponseReader.Ok(in reply));
}
