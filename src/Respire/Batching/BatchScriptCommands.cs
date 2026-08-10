using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// Lua scripts queued on a <see cref="RespireBatch"/> or <see cref="RespireTransaction"/>.
/// Deferred scripts use EVAL directly so they are valid inside MULTI/EXEC without requiring a
/// post-execution NOSCRIPT retry.
/// </summary>
public interface IBatchScriptCommands
{
    /// <summary>
    /// Queues a script evaluation. Keys are prefixed by the client and args are passed through
    /// ARGV. The result is a lease and must be disposed. Redis: EVAL.
    /// </summary>
    RespirePending<RespireResult> Evaluate(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null);
}

internal sealed class BatchScriptCommands(IPendingSink sink) : IBatchScriptCommands
{
    public RespirePending<RespireResult> Evaluate(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = sink.Client.BuildScriptTail(keys, args);
        var command = new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]);
        return sink.Add<Cmd2N, RespireResult>(
            "EVAL",
            command,
            keys.AsSpan(),
            static (c, value) =>
            {
                var owned = value.ToOwned();
                return new RespireResult(in owned);
            });
    }
}
