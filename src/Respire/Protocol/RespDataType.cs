namespace Respire.Protocol;

/// <summary>The RESP2/RESP3 wire type represented by a <see cref="RespireResult"/>.</summary>
public enum RespDataType : byte
{
    /// <summary>No parsed value.</summary>
    None = 0,
    
    // RESP2 Types
    /// <summary>RESP simple string (<c>+</c>).</summary>
    SimpleString = (byte)'+',
    /// <summary>RESP simple error (<c>-</c>).</summary>
    Error = (byte)'-',
    /// <summary>RESP integer (<c>:</c>).</summary>
    Integer = (byte)':',
    /// <summary>RESP bulk string (<c>$</c>).</summary>
    BulkString = (byte)'$',
    /// <summary>RESP array (<c>*</c>).</summary>
    Array = (byte)'*',
    
    // RESP3 Types  
    /// <summary>RESP null (<c>_</c>).</summary>
    Null = (byte)'_',
    /// <summary>RESP boolean (<c>#</c>).</summary>
    Boolean = (byte)'#',
    /// <summary>RESP double (<c>,</c>).</summary>
    Double = (byte)',',
    /// <summary>RESP big number (<c>(</c>).</summary>
    BigNumber = (byte)'(',
    /// <summary>RESP bulk error (<c>!</c>).</summary>
    BulkError = (byte)'!',
    /// <summary>RESP verbatim string (<c>=</c>).</summary>
    VerbatimString = (byte)'=',
    /// <summary>RESP map (<c>%</c>).</summary>
    Map = (byte)'%',
    /// <summary>RESP set (<c>~</c>).</summary>
    Set = (byte)'~',
    /// <summary>RESP push frame (<c>&gt;</c>).</summary>
    Push = (byte)'>',
    
    // Special
    /// <summary>RESP attribute frame (<c>|</c>).</summary>
    Attribute = (byte)'|',
    /// <summary>RESP streamed-string chunk (<c>;</c>).</summary>
    StreamedString = (byte)';',
    /// <summary>Parsed HELLO handshake response.</summary>
    Hello = (byte)'H'
}
