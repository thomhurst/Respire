namespace Respire.Internal;

// Share transitions without changing owner layouts, lifetimes or synchronization gates.
// Capture counts under the owner's gate; publish telemetry only after releasing it.
internal static class ErrorPublication
{
    internal static void SetAttempts(ref int attempts, bool finalPublished, int value)
    {
        if (!finalPublished) attempts = Math.Max(0, value);
    }

    internal static bool TryRecordHandled(ref int attempts, bool finalPublished, out int eventAttempts)
    {
        eventAttempts = attempts;
        return TryRecordRetry(ref attempts, finalPublished);
    }

    internal static bool TryRecordRetry(ref int attempts, bool finalPublished)
    {
        if (finalPublished) return false;
        if (attempts < int.MaxValue) attempts++;
        return true;
    }

    internal static bool TryPublishFinal(int attempts, ref bool finalPublished, out int eventAttempts)
    {
        eventAttempts = attempts;
        if (finalPublished) return false;
        finalPublished = true;
        return true;
    }
}
