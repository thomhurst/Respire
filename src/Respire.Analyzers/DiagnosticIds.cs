namespace Respire.Analyzers;

/// <summary>Diagnostic ids shipped with the Respire package.</summary>
public static class DiagnosticIds
{
    /// <summary>A pooled <c>RespireResult</c>/<c>RespireLease</c> is never disposed.</summary>
    public const string UndisposedPooledResult = "RESP001";

    /// <summary>A <c>RespirePending{T}</c> is read before its batch/transaction is flushed.</summary>
    public const string PendingReadBeforeFlush = "RESP002";

    /// <summary>A <c>[RespireCommands]</c> interface or method cannot be generated.</summary>
    public const string InvalidGeneratedCommand = "RESP003";

    internal const string Category = "Respire";
}
