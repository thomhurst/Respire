namespace Respire.Testing;

/// <summary>A RESP reply for a command expectation or registered script handler.
/// Expectation overrides do not execute the fake command handler or mutate its data.</summary>
public sealed class RespireFakeResponse
{
    internal FakeReply Reply { get; }
    private RespireFakeResponse(FakeReply reply) => Reply = reply;

    /// <summary>A null bulk reply (RESP2) or null (RESP3).</summary>
    public static RespireFakeResponse Null { get; } = new(FakeReply.Null);

    /// <summary>Creates a simple string reply, such as OK.</summary>
    public static RespireFakeResponse Simple(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("Simple replies cannot contain CR or LF.", nameof(value));
        return new(FakeReply.Simple(value));
    }

    /// <summary>Creates a UTF-8 bulk string reply.</summary>
    public static RespireFakeResponse Bulk(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(FakeReply.Text(value));
    }

    /// <summary>Copies bytes into a bulk string reply.</summary>
    public static RespireFakeResponse Bulk(ReadOnlyMemory<byte> value) => new(FakeReply.Bulk(value.ToArray()));

    /// <summary>Creates an integer reply.</summary>
    public static RespireFakeResponse Integer(long value) => new(FakeReply.Integer(value));

    /// <summary>Creates an array reply, including nested arrays and error elements.</summary>
    public static RespireFakeResponse Array(params RespireFakeResponse[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(value => value is null)) throw new ArgumentException("Replies cannot be null; use Null.", nameof(values));
        return new(FakeReply.Array(values.Select(value => value.Reply).ToArray()));
    }

    /// <summary>Creates an error reply. CR and LF are replaced with spaces, as in fault injection.</summary>
    public static RespireFakeResponse Error(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(FakeReply.Error(message));
    }
}
