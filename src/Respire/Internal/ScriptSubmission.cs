namespace Respire.Internal;

// A stateful script honors caller cancellation up to transport admission. After that,
// its owner needs the reply to reconcile local state with the server.
internal sealed class ScriptSubmission(CancellationToken cancellationToken)
{
    private bool _wasAccepted;

    internal CancellationToken CancellationToken => cancellationToken;

    internal bool WasAccepted => Volatile.Read(ref _wasAccepted);

    internal void MarkAccepted()
    {
        Volatile.Write(ref _wasAccepted, true);
    }
}
