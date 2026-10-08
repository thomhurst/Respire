using Respire.Internal;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>Identifies connection-owned session setup, never an application data mutation.</summary>
internal interface IConnectionProtocolCommand : IRespCommand
{
    bool IRespCommand.IsConnectionProtocol => true;
}

/// <summary>Explicit admission for an internal connection protocol step.</summary>
internal readonly struct ProtocolCommand<TCommand>(TCommand command) : IRespCommandWrapper, IConnectionProtocolCommand
    where TCommand : struct, IRespCommand
{
    public void Write(ref RespWriter writer) => command.Write(ref writer);
    public int GetWriteSizeHint() => command.GetWriteSizeHint();
    public ReadCommandKind ReadKind => command.ReadKind;
    public bool IsConnectionProtocol => true;
    public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    public void OnAccepted() => command.OnAccepted();
    public void ValidateAdmission() => command.ValidateAdmission();
    public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken)
        => command.GetResponseCancellationToken(admissionToken);
    public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
}
