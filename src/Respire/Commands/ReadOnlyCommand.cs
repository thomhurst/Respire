using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Carries an audited read-only declaration without changing the underlying wire command.</summary>
internal readonly struct ReadOnlyCommand<TCommand>(TCommand command, bool declaredReadOnly = true) : IRespCommandWrapper
    where TCommand : struct, IRespCommand
{
    public void Write(ref RespWriter writer) => command.Write(ref writer);
    public int GetWriteSizeHint() => command.GetWriteSizeHint();
    public ReadCommandKind ReadKind => command.ReadKind;
    public bool IsConnectionProtocol => CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command);
    public int CursorArgumentIndex => command.CursorArgumentIndex;
    public void OnAccepted() => command.OnAccepted();
    public void ValidateAdmission() => command.ValidateAdmission();
    public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken)
        => command.GetResponseCancellationToken(admissionToken);
    public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
    public RespireCacheMutation GetCacheMutation(string operation)
        => declaredReadOnly ? RespireCacheMutation.ReadOnly : command.GetCacheMutation(operation);
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);
    public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);
    public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
        => command.TryGetClientCacheKey(operation, out key);
    public bool TryGetArgument(int index, out RespireValue value) => command.TryGetArgument(index, out value);
}
