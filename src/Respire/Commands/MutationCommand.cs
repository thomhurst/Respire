using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Keeps a mutation's owned fence on every native attempt, including redirects.</summary>
internal readonly struct MutationCommand<TCommand>(TCommand command,
    ClientSideCacheCoordinator.MutationFence fence) : IRespCommandWrapper
    where TCommand : struct, IRespCommand
{
    public void Write(ref RespWriter writer) => command.Write(ref writer);
    public bool IsConnectionProtocol => CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command);
    public int GetWriteSizeHint() => command.GetWriteSizeHint();
    public void OnAccepted() => command.OnAccepted();
    public void ValidateAdmission() => command.ValidateAdmission();
    public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken)
        => command.GetResponseCancellationToken(admissionToken);
    public ClientSideCacheCoordinator.MutationFence GetMutationFence() => fence;
    public ReadCommandKind ReadKind => command.ReadKind;
    public int CursorArgumentIndex => command.CursorArgumentIndex;
    public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);
    public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);
    public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
        => command.TryGetClientCacheKey(operation, out key);
    public bool TryGetArgument(int index, out RespireValue value) => command.TryGetArgument(index, out value);
}
