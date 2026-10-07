using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// Lua scripts queued on a <see cref="RespireBatch"/> or <see cref="RespireTransaction"/>.
/// Deferred scripts use EVAL (EVAL_RO for read-only scripts) directly so they are valid
/// inside MULTI/EXEC without requiring a post-execution NOSCRIPT retry.
/// </summary>
public interface IBatchScriptCommands
{
    /// <summary>
    /// Queues a script evaluation. Keys are prefixed by the client and args are passed through
    /// ARGV. The result owns GC-managed storage, so disposal is optional and unread results do not
    /// retain pooled reply buffers. Disposing a result invalidates it and its nested views. Redis: EVAL / EVAL_RO.
    /// </summary>
    RespirePending<RespireResult> Evaluate(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null);

    /// <summary>Queues SCRIPT LOAD on the execution node. Cluster execution does not fan out.</summary>
    RespirePending<string> Load(RespireScript script)
        => throw new NotSupportedException("This implementation does not support deferred SCRIPT LOAD.");

    /// <summary>Queues SCRIPT EXISTS on the execution node. Results follow input digest order.</summary>
    RespirePending<bool[]> Exists(params ReadOnlySpan<string> sha1s)
        => throw new NotSupportedException("This implementation does not support deferred SCRIPT EXISTS.");

    /// <summary>
    /// Queues SCRIPT FLUSH on the execution node. In Cluster, this clears only that node's cache;
    /// use IScriptCommands.FlushAsync for all discovered primaries. Returns true for OK.
    /// </summary>
    RespirePending<bool> Flush(ScriptFlushMode mode = ScriptFlushMode.Default)
        => throw new NotSupportedException("This implementation does not support deferred SCRIPT FLUSH.");
}

internal sealed class BatchScriptCommands(IPendingSink sink) : IBatchScriptCommands
{
    public RespirePending<string> Load(RespireScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return sink.Add<Cmd1, string>("SCRIPT LOAD", new Cmd1(Verbs.ScriptLoad, script.Source),
            static (_, value) => ResponseReader.String(in value));
    }

    public RespirePending<bool[]> Exists(params ReadOnlySpan<string> sha1s)
        => sink.Add<CmdN, bool[]>("SCRIPT EXISTS", new CmdN(Verbs.ScriptExists, ScriptCommands.MapDigests(sha1s)),
            static (_, value) => ResponseReader.FlagArray(in value));

    public RespirePending<bool> Flush(ScriptFlushMode mode = ScriptFlushMode.Default)
        => sink.Add<Cmd, bool>("SCRIPT FLUSH", new Cmd(ScriptCommands.FlushVerb(mode)),
            static (_, value) => ResponseReader.Ok(in value));

    public RespirePending<RespireResult> Evaluate(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = sink.Client.BuildScriptTail(keys, args);
        var command = new BatchScriptCommand(script.EvalVerb, script.Source, tail, keys?.Length ?? 0);
        return sink.Add<BatchScriptCommand, RespireResult>(
            script.EvalOperation,
            command,
            keys.AsSpan(),
            static (client, value) => RespireResult.CreateOwned(in value, client.Core.Options.Serializer));
    }
}

internal readonly struct BatchScriptCommand(
    Verb verb, RespireValue source, RespireValue[] tail, int keyCount) : IRespCommand
{
    public int GetWriteSizeHint() => CommandWriteSizeHint.Add(
        CommandWriteSizeHint.For(verb, source.GetWriteSizeHint()), tail);
    public bool TryGetArgument(int index, out RespireValue value)
    {
        if (index == 0) { value = source; return true; }
        if ((uint)(index - 1) < (uint)tail.Length) { value = tail[index - 1]; return true; }
        value = default;
        return false;
    }

    public ReadCommandKind ReadKind => verb.ReadKind;

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
