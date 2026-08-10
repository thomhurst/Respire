namespace Respire;

/// <summary>Policy flags for command execution.</summary>
[Flags]
public enum RespireCommandFlags
{
    /// <summary>Use the default command behavior.</summary>
    None = 0,

    /// <summary>
    /// In Redis Cluster mode, surface MOVED and ASK replies instead of following them.
    /// </summary>
    NoRedirect = 1,
}
