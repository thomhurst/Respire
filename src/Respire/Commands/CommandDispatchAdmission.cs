using System.Runtime.CompilerServices;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Skips default interface calls that would box ordinary command values.</summary>
internal static class CommandDispatchAdmission<TCommand> where TCommand : struct, IRespCommand
{
    private static readonly bool CarriesFence = typeof(IMutationAdmissionCommand).IsAssignableFrom(typeof(TCommand));
    private static readonly bool IsProtocol = typeof(IConnectionProtocolCommand).IsAssignableFrom(typeof(TCommand));
    private static readonly bool ForwardsProtocol = typeof(IRespCommandWrapper).IsAssignableFrom(typeof(TCommand));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ClientSideCacheCoordinator.MutationFence GetMutationFence(in TCommand command)
        => CarriesFence ? command.GetMutationFence() : default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsConnectionProtocol(in TCommand command)
        => IsProtocol || ForwardsProtocol && command.IsConnectionProtocol;
}
