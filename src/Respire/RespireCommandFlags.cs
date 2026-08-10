namespace Respire;

/// <summary>Policy flags for raw and catalog command execution.</summary>
[Flags]
public enum RespireCommandFlags
{
    /// <summary>Use the default command behavior.</summary>
    None = 0,

    /// <summary>
    /// Queue the command without delivering its reply to the caller. Use
    /// <see cref="IRespireClient.ExecuteFireAndForgetAsync(RespireCommand, RespireValue[])"/>
    /// or <see cref="IRespireClient.ExecuteFireAndForgetAsync(string, RespireValue[])"/> for
    /// a no-result API shape.
    /// </summary>
    FireAndForget = 1,

    /// <summary>
    /// In Redis Cluster mode, surface MOVED and ASK replies instead of following them.
    /// </summary>
    NoRedirect = 2,
}
