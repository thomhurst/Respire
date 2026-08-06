namespace Respire.Protocol;

/// <summary>Shared constants for RESP protocol serialization.</summary>
internal static class RespConstants
{
    public const byte BulkStringPrefix = (byte)'$';
    public const byte ArrayPrefix = (byte)'*';
    public const byte CarriageReturn = 13;
    public const byte LineFeed = 10;
}
