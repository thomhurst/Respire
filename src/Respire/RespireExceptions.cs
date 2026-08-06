namespace Respire;

/// <summary>Base exception for all Respire failures.</summary>
public class RespireException : Exception
{
    public RespireException(string message) : base(message)
    {
    }

    public RespireException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The connection failed, was closed by the peer, or was disposed with commands in flight.</summary>
public class RespireConnectionException : RespireException
{
    public RespireConnectionException(string message) : base(message)
    {
    }

    public RespireConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The byte stream violated the RESP protocol; the connection is no longer usable.</summary>
public sealed class RespireProtocolException(string message) : RespireException(message);

/// <summary>The server answered a command with a RESP error reply ("-ERR ...").</summary>
public sealed class RespireServerException(string message) : RespireException(message);
