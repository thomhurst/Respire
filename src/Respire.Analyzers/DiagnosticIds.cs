namespace Respire.Analyzers;

/// <summary>Diagnostic ids shipped with the Respire package.</summary>
public static class DiagnosticIds
{
    /// <summary>A pooled <c>RespireResult</c>/<c>RespireLease</c> is never disposed.</summary>
    public const string UndisposedPooledResult = "RESP001";

    /// <summary>A <c>RespirePending{T}</c> is read before its batch/transaction is flushed.</summary>
    public const string PendingReadBeforeFlush = "RESP002";

    internal const string Category = "Respire";
}
