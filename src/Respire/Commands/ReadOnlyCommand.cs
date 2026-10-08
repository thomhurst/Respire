using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Carries an audited read-only declaration without changing the underlying wire command.</summary>
internal readonly struct ReadOnlyCommand<TCommand> : IRespCommandWrapper
    where TCommand : struct, IRespCommand
{
    private readonly TCommand _command;
    private readonly bool _declaredReadOnly;

    private ReadOnlyCommand(TCommand command, bool declaredReadOnly)
    {
        _command = command;
        _declaredReadOnly = declaredReadOnly;
    }

    // Source guards restrict these declarations to their audited script and node paths.
    internal static ReadOnlyCommand<TCommand> ForAuditedScript(TCommand command, bool declaredReadOnly)
        => new(command, declaredReadOnly);

    internal static ReadOnlyCommand<TCommand> ForNodeRead(TCommand command, bool declaredReadOnly)
        => new(command, declaredReadOnly);

    public void Write(ref RespWriter writer) => _command.Write(ref writer);
    public int GetWriteSizeHint() => _command.GetWriteSizeHint();
    public ReadCommandKind ReadKind => _command.ReadKind;
    public bool IsConnectionProtocol => CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in _command);
    public int CursorArgumentIndex => _command.CursorArgumentIndex;
    public void OnAccepted() => _command.OnAccepted();
    public void ValidateAdmission() => _command.ValidateAdmission();
    public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken)
        => _command.GetResponseCancellationToken(admissionToken);
    public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in _command);
    public RespireCacheMutation GetCacheMutation(string operation)
        => _declaredReadOnly ? RespireCacheMutation.ReadOnly : _command.GetCacheMutation(operation);
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => _command.GetClientCacheMetadata(operation);
    public bool TryGetPrimaryKey(out RespireValue key) => _command.TryGetPrimaryKey(out key);
    public bool TryGetClusterSlot(out int slot) => _command.TryGetClusterSlot(out slot);
    public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
        => _command.TryGetClientCacheKey(operation, out key);
    public bool TryGetArgument(int index, out RespireValue value) => _command.TryGetArgument(index, out value);
}
