using System.Text;
using Respire.Protocol;

namespace Respire.Testing;

/// <summary>Runs synchronous data commands against a registered script's atomic fake keyspace.</summary>
/// <remarks>Use only on the handler's thread during its invocation. Do not block on client operations.
/// Internal calls bypass command expectations and fault injection. Dispose returned values.</remarks>
public sealed class RespireFakeScriptContext
{
    private Func<byte[][], FakeReply>? _execute;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    internal RespireFakeScriptContext(Func<byte[][], FakeReply> execute) => _execute = execute;

    /// <summary>Executes a supported nonblocking data command, like Lua redis.call.</summary>
    /// <remarks>Arguments are copied. Connection, transaction, scripting and blocking commands are rejected.
    /// Server errors throw <see cref="RespireServerException"/>; earlier writes are not rolled back.</remarks>
    public RespireResult Call(string command, params RespireValue[] arguments)
    {
        if (_execute is null || Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("The script context is valid only on the handler's thread during its invocation.");
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        var wire = new byte[arguments.Length + 1][];
        wire[0] = Encoding.UTF8.GetBytes(command);
        for (var index = 0; index < arguments.Length; index++)
        {
            RespireValue.ThrowIfNull(arguments[index], nameof(arguments));
            wire[index + 1] = new byte[arguments[index].GetWireLength()];
            arguments[index].WriteWirePayload(wire[index + 1]);
        }
        var reply = _execute(wire);
        if (reply.Prefix == '-') throw new RespireServerException((string)reply.Value!, command);
        var bytes = reply.Encode(resp3: false);
        var consumed = 0;
        if (RespParser.TryParseValue(bytes, ref consumed, out var value) != RespParseStatus.Done)
            throw new InvalidOperationException("The fake command returned an invalid reply.");
        return new RespireResult(in value);
    }

    internal void Invalidate() => _execute = null;
}
