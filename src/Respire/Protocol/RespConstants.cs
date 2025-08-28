namespace Respire.Protocol;

/// <summary>
/// Shared constants for RESP protocol parsing and serialization
/// Centralizes common byte patterns to avoid duplicate allocations
/// </summary>
internal static class RespConstants
{
    /// <summary>
    /// CRLF delimiter used throughout RESP protocol
    /// Single allocation shared across all components
    /// </summary>
    public static readonly byte[] CRLF = { 13, 10 }; // ASCII \r\n
    
    /// <summary>
    /// Common RESP protocol type prefixes
    /// </summary>
    public const byte SimpleStringPrefix = (byte)'+';
    public const byte ErrorPrefix = (byte)'-';
    public const byte IntegerPrefix = (byte)':';
    public const byte BulkStringPrefix = (byte)'$';
    public const byte ArrayPrefix = (byte)'*';
    public const byte BooleanPrefix = (byte)'#';
    
    /// <summary>
    /// Common RESP protocol characters
    /// </summary>
    public const byte CarriageReturn = 13;  // \r
    public const byte LineFeed = 10;         // \n
    public const byte NullBulkStringIndicator = (byte)'-';
    public const byte TrueIndicator = (byte)'t';
    public const byte FalseIndicator = (byte)'f';
}