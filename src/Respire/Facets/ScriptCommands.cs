using System.Security.Cryptography;
using System.Text;
using Respire.Commands;

namespace Respire;

/// <summary>
/// A Lua script with its SHA1 precomputed. Create once (static readonly), execute many times —
/// execution tries EVALSHA first and transparently falls back to EVAL (which caches the script
/// server-side) the first time a server hasn't seen it.
/// </summary>
public sealed class RespireScript
{
    private RespireScript(string source, string sha1)
    {
        Source = source;
        Sha1 = sha1;
    }

    public string Source { get; }

    public string Sha1 { get; }

    public static RespireScript Create(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var sha = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source)));
        return new RespireScript(source, sha.ToLowerInvariant());
    }
}

/// <summary>Lua scripting commands.</summary>
public interface IScriptCommands
{
    /// <summary>
    /// Executes a script. Keys go through KEYS[…] (and get this view's key prefix); args through
    /// ARGV[…]. The result is a lease — dispose it. Redis: EVALSHA / EVAL.
    /// </summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a script into the server cache and returns its SHA1. Redis: SCRIPT LOAD.</summary>
    ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default);
}

internal sealed class ScriptCommands(RespireClient client) : IScriptCommands
{
    public async ValueTask<RespireResult> ExecuteAsync(
        RespireScript script, RespireKey[]? keys = null, RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = client.BuildScriptTail(keys, args);
        try
        {
            var reply = await client.SendAsync(
                "EVALSHA", new Cmd2N(Verbs.EvalSha, script.Sha1, tail[0], tail[1..]), cancellationToken)
                .ConfigureAwait(false);
            return new RespireResult(in reply);
        }
        catch (RespireServerException ex) when (ex.Code == "NOSCRIPT")
        {
            var reply = await client.SendAsync(
                "EVAL", new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]), cancellationToken)
                .ConfigureAwait(false);
            return new RespireResult(in reply);
        }
    }

    public ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        return client.StringAsync("SCRIPT", new Cmd1(Verbs.ScriptLoad, script.Source), cancellationToken);
    }

}
