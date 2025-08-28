namespace Respire.Protocol;

public enum RespDataType : byte
{
    None = 0,
    
    // RESP2 Types
    SimpleString = (byte)'+',
    Error = (byte)'-',
    Integer = (byte)':',
    BulkString = (byte)'$',
    Array = (byte)'*',
    
    // RESP3 Types  
    Null = (byte)'_',
    Boolean = (byte)'#',
    Double = (byte)',',
    BigNumber = (byte)'(',
    BulkError = (byte)'!',
    VerbatimString = (byte)'=',
    Map = (byte)'%',
    Set = (byte)'~',
    Push = (byte)'>',
    
    // Special
    Attribute = (byte)'|',
    StreamedString = (byte)';',
    Hello = (byte)'H'
}