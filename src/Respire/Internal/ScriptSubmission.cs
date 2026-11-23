namespace Respire.Internal;

// The owner drains admitted replies. Migrations also finish redirect recovery after admission
// so an acknowledged server-side change can be applied locally.
internal sealed class ScriptSubmission(CancellationToken cancellationToken, bool completeAfterAcceptance = false)
{
    private bool _wasAccepted;

    internal CancellationToken CancellationToken => cancellationToken;

    internal bool WasAccepted => Volatile.Read(ref _wasAccepted);

    internal bool IsCancellationDetached => completeAfterAcceptance && WasAccepted;

    internal void MarkAccepted()
    {
        Volatile.Write(ref _wasAccepted, true);
    }
}
