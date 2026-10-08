using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    /// <summary>Every native dispatch on a logical client's transport must be admitted before publication.</summary>
    internal void ValidateDispatchAdmission<TCommand>(in TCommand command)
        where TCommand : struct, IRespCommand
    {
        var fence = CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
        if (fence.IsRequired)
        {
            if (!fence.IsLiveFor(this))
                throw new InvalidOperationException("Native dispatch requires a live mutation admission from its owning client.");
            return;
        }
        if (CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command)) return;
        // Explicit read-only declarations remain supported, except effects known to write
        // indirect keys. Unknown commands retain conservative mutation ownership.
        // Diagnostic command names cannot authorize a dispatch. Unclassified command
        // types stay unknown unless they carry an explicit read-only declaration.
        if (command.GetCacheMutation(string.Empty) == RespireCacheMutation.ReadOnly
            && command.GetClientCacheMetadata(string.Empty).MutationKind != RawCommandKeyLayouts.MutationKind.IndirectKeys)
            return;
        throw new InvalidOperationException($"Native dispatch of {typeof(TCommand).Name} bypassed the logical client's cache mutation admission.");
    }
}
