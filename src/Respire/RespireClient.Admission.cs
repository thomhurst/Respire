using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // Adapter dispatch can release its ordering gate after FIFO publication, without waiting for the reply.
    internal ValueTask<RespireResult> ExecuteWithAdmissionAsync(RespireCommand command, RespireValue[] arguments,
        RespireCommandFlags flags, CancellationToken cancellationToken, TaskCompletionSource admission)
        => ExecuteCatalogAsync(command, arguments, flags, cancellationToken, admission);

    private readonly struct AdmissionCommand(CatalogCommand command, TaskCompletionSource admission) : IRespCommandWrapper
    {
        public void Write(ref RespWriter writer) => command.Write(ref writer);
        public int GetWriteSizeHint() => command.GetWriteSizeHint();
        public ReadCommandKind ReadKind => command.ReadKind;
        public int CursorArgumentIndex => command.CursorArgumentIndex;
        public bool IsConnectionProtocol => false;
        // CatalogCommand owns no fence. SendCoreAsync applies MutationCommand outside this decorator,
        // using the forwarded mutation metadata, and retains that fence through native FIFO retirement.
        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => default;
        public void ValidateAdmission() { }
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;
        public void OnAccepted() => admission.TrySetResult();
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
        public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);
        public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);
        public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
            => command.TryGetClientCacheKey(operation, out key);
        public bool TryGetArgument(int index, out RespireValue value) => command.TryGetArgument(index, out value);
    }
}
