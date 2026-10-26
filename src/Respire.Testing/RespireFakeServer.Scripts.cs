using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private sealed record RegisteredScript(RespireScript Script,
        Func<RespireFakeScriptContext, IReadOnlyList<RespireValue>, IReadOnlyList<RespireValue>, RespireFakeResponse> Handler);

    private readonly Dictionary<string, RegisteredScript> _registeredScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scriptCache = new(StringComparer.OrdinalIgnoreCase);

    // These commands affect connection state, recurse into scripts, or can wait for another command.
    private static readonly HashSet<string> ScriptForbiddenCommands = new(StringComparer.Ordinal)
    {
        "HELLO", "CLIENT", "SELECT", "SUBSCRIBE", "UNSUBSCRIBE", "MULTI", "EXEC", "DISCARD",
        "WATCH", "UNWATCH", "EVAL", "EVALSHA", "EVAL_RO", "EVALSHA_RO", "SCRIPT", "BLMOVEM",
        "XREAD", "XREADGROUP", "HIMPORT"
    };

    /// <summary>Registers a synchronous C# implementation for an exact Lua source and its SHA1.</summary>
    /// <remarks>Registration does not load the server script cache. EVALSHA returns NOSCRIPT until EVAL or
    /// SCRIPT LOAD caches the script. Handlers run under the command lock against the shared keyspace.
    /// Duplicate registrations throw. Unregistered scripts still fail; no Lua interpreter is embedded.</remarks>
    public void RegisterScript(RespireScript script,
        Func<RespireFakeScriptContext, IReadOnlyList<RespireValue>, IReadOnlyList<RespireValue>, RespireFakeResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registeredScripts.Add(script.Sha1, new(script, handler));
        }
    }

    private RegisteredScript? FindRegisteredScript(byte[] source, bool sha)
    {
        if (sha) return _registeredScripts.GetValueOrDefault(Encoding.ASCII.GetString(source));
        return _registeredScripts.Values.FirstOrDefault(candidate =>
            source.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(candidate.Script.Source)));
    }

    private FakeReply ExecuteScript(Connection connection, byte[][] args, bool sha)
    {
        var keyCount = Integer(args[2]);
        if (keyCount < 0) return FakeReply.Error("ERR Number of keys can't be negative");
        if (keyCount > args.Length - 3) return FakeReply.Error("ERR Number of keys can't be greater than number of args");
        var registered = FindRegisteredScript(args[1], sha);
        if (registered is null) return BuiltInScript(args, sha);
        if (sha && !_scriptCache.Contains(registered.Script.Sha1))
            return FakeReply.Error("NOSCRIPT No matching script. Please use EVAL.");
        _scriptCache.Add(registered.Script.Sha1);
        var values = args.Skip(3).Select(value => new RespireValue(value.AsMemory())).ToArray();
        var count = (int)keyCount;
        var context = new RespireFakeScriptContext(command =>
        {
            var name = Token(command[0]);
            if (ScriptForbiddenCommands.Contains(name))
                return FakeReply.Error($"ERR Respire.Testing does not support {name} inside a script handler");
            return Execute(connection, command);
        });
        try
        {
            return registered.Handler(context, System.Array.AsReadOnly(values[..count]),
                System.Array.AsReadOnly(values[count..])).Reply;
        }
        catch (RespireServerException error) { return FakeReply.Error(error.Message); }
        finally { context.Invalidate(); }
    }

    private FakeReply ScriptCache(byte[][] args)
    {
        switch (Token(args[1]))
        {
            case "LOAD" when args.Length == 3:
                var registered = FindRegisteredScript(args[2], sha: false);
                if (registered is null) return UnsupportedScript();
                _scriptCache.Add(registered.Script.Sha1);
                return FakeReply.Text(registered.Script.Sha1);
            case "EXISTS" when args.Length >= 3:
                return FakeReply.Array(args.Skip(2).Select(sha =>
                    FakeReply.Integer(_scriptCache.Contains(Encoding.ASCII.GetString(sha)) ? 1 : 0)).ToArray());
            case "FLUSH" when args.Length == 2 || args.Length == 3 && Token(args[2]) is "SYNC" or "ASYNC":
                _scriptCache.Clear();
                return FakeReply.Ok;
            default:
                return Syntax("SCRIPT");
        }
    }

    private static FakeReply UnsupportedScript()
        => FakeReply.Error("ERR Respire.Testing unsupported script; register an exact application script handler. The fake does not embed a Lua interpreter.");
}
