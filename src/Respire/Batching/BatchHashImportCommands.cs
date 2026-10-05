using Respire.Commands;
using Respire.Internal;

namespace Respire;

public partial interface IBatchHashCommands
{
    /// <summary>Queues HIMPORT PREPARE on a hash import session's batch or transaction.</summary>
    RespirePending<bool> PrepareImport(RespireValue name, params ReadOnlySpan<RespireValue> fields);

    /// <summary>Queues HIMPORT SET on a hash import session's batch or transaction.</summary>
    RespirePending<bool> Import(RespireKey key, RespireValue name, params ReadOnlySpan<RespireValue> values);

    /// <summary>Queues HIMPORT DISCARD; the pending reports whether the fieldset existed.</summary>
    RespirePending<bool> DiscardImport(RespireValue name);

    /// <summary>Queues HIMPORT DISCARDALL; the pending reports the number removed.</summary>
    RespirePending<long> DiscardAllImports();
}

internal sealed partial class BatchHashCommands
{
    private RespireHashImportSession ImportSession => sink.ImportSession
        ?? throw new InvalidOperationException("Create this queue through a hash import session to preserve connection-local fieldsets.");

    public RespirePending<bool> PrepareImport(RespireValue name, params ReadOnlySpan<RespireValue> fields)
        => sink.Add<CmdN, bool>("HIMPORT PREPARE", ImportSession.PrepareCommand(name, fields),
            static (_, value) => ResponseReader.Ok(in value));

    public RespirePending<bool> Import(RespireKey key, RespireValue name, params ReadOnlySpan<RespireValue> values)
        => sink.Add<CmdN, bool>("HIMPORT SET", ImportSession.SetCommand(key, name, values),
            static (_, value) => ResponseReader.Ok(in value));

    public RespirePending<bool> DiscardImport(RespireValue name)
        => sink.Add<CmdN, bool>("HIMPORT DISCARD", ImportSession.DiscardCommand(name),
            static (_, value) => RespireHashImportSession.ReadDiscard(in value));

    public RespirePending<long> DiscardAllImports()
        => sink.Add<CmdN, long>("HIMPORT DISCARDALL", ImportSession.DiscardAllCommand(),
            static (_, value) => RespireHashImportSession.ReadDiscardAll(in value));
}
