namespace Respire;

/// <summary>Negotiation policy for RESP3 server maintenance notifications.</summary>
public enum RespireMaintenanceNotificationMode
{
    /// <summary>Do not request or act on maintenance notifications (default).</summary>
    Disabled,
    /// <summary>Request notifications on RESP3 command connections; tolerate unsupported servers.</summary>
    Auto,
    /// <summary>Require successful negotiation on command connections. RESP2 is not supported.</summary>
    Enabled,
}
