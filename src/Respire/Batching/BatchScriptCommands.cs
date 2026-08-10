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
        var command = new BatchScriptCommand(Verbs.Eval, script.Source, tail, keys?.Length ?? 0);
        return sink.Add<BatchScriptCommand, RespireResult>(
            "EVAL",
            command,
            keys.AsSpan(),
            static (client, value) =>
            {
                var owned = value.ToOwned();
                return client.CreateResult(in owned);
            });
    }
}

internal readonly struct BatchScriptCommand(
    Verb verb, RespireValue source, RespireValue[] tail, int keyCount) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        if (keyCount > 0 && tail.Length > 1)
        {
            return tail[1].TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 1 + tail.Length);
        writer.WriteRaw(verb.Bulk);
        source.WriteTo(ref writer);
        foreach (var value in tail)
        {
            value.WriteTo(ref writer);
        }
    }
}
