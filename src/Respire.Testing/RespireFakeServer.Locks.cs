using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    // Mirror the exact script in Respire.Coordination without making this testing package
    // depend on every optional feature package. Arbitrary Lua remains unsupported.
    private static readonly RespireScript FencedLockAcquireScript = RespireScript.Create("""
        -- Keep the invariant even when this script is invoked without the managed entry point.
        if KEYS[1] == KEYS[2] then
            return redis.error_reply('ERR lock and fencing counter keys must differ')
        end
        if redis.call('EXISTS', KEYS[1]) == 1 then return false end
        local counter = redis.call('GET', KEYS[2])
        if counter and (not string.match(counter, '^%d+$') or (#counter > 1 and string.sub(counter, 1, 1) == '0')) then
            return redis.error_reply('ERR fencing counter must be a nonnegative canonical integer')
        end
        if redis.call('PTTL', KEYS[2]) >= 0 then
            return redis.error_reply('ERR fencing counter must not expire')
        end
        -- INCR rejects Int64 overflow (including oversized digit strings) before creating a lease.
        redis.call('INCR', KEYS[2])
        local fence = redis.call('GET', KEYS[2])
        redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2])
        return fence
        """);
    // Raw C# literals preserve checkout line endings, including when optional packages
    // and this package were built on different platforms.
    private static readonly RespireScript FencedLockAcquireLfScript =
        RespireScript.Create(FencedLockAcquireScript.Source.ReplaceLineEndings("\n"));
    private static readonly RespireScript FencedLockAcquireCrLfScript =
        RespireScript.Create(FencedLockAcquireScript.Source.ReplaceLineEndings("\r\n"));

    private FakeReply BuiltInScript(byte[][] args, bool sha)
    {
        var source = Encoding.UTF8.GetString(args[1]);
        RespireScript? script = Matches(FencedLockAcquireLfScript) ? FencedLockAcquireLfScript
            : Matches(FencedLockAcquireCrLfScript) ? FencedLockAcquireCrLfScript
            : Matches(LockCommands.ReleaseScript) ? LockCommands.ReleaseScript
            : Matches(LockCommands.ExtendScript) ? LockCommands.ExtendScript : null;
        if (script is null) return StreamWorkerScript(args, sha);
        if (sha && !_workerScriptCache.Contains(source))
            return FakeReply.Error("NOSCRIPT No matching script. Please use EVAL.");

        var acquire = script == FencedLockAcquireLfScript || script == FencedLockAcquireCrLfScript;
        var extend = script == LockCommands.ExtendScript;
        if (Integer(args[2]) != (acquire ? 2 : 1) || args.Length != (acquire ? 7 : extend ? 6 : 5))
            return Syntax("EVAL");
        if (!sha) _workerScriptCache.Add(script.Sha1);
        if (acquire) return AcquireFencedLock(args);

        var value = Find(args[3])?.Value;
        if (value is null || !value.AsSpan().SequenceEqual(args[4])) return FakeReply.Integer(0);
        return extend ? Expire("PEXPIRE", ["PEXPIRE"u8.ToArray(), args[3], args[5]])
            : FakeReply.Integer(DeleteEntry(args[3]) ? 1 : 0);

        bool Matches(RespireScript candidate) => (sha ? candidate.Sha1 : candidate.Source) == source;
    }

    private FakeReply AcquireFencedLock(byte[][] args)
    {
        var key = args[3];
        var counterKey = args[4];
        if (key.AsSpan().SequenceEqual(counterKey))
            return FakeReply.Error("ERR lock and fencing counter keys must differ");
        if (Find(key) is not null) return FakeReply.Null;
        var counter = Find(counterKey);
        if (counter is not null)
        {
            var value = counter.Value;
            if (value.Length == 0 || value.Length > 1 && value[0] == '0' || value.Any(b => b < '0' || b > '9'))
                return FakeReply.Error("ERR fencing counter must be a nonnegative canonical integer");
            if (counter.ExpiresAt is not null)
                return FakeReply.Error("ERR fencing counter must not expire");
        }
        Increment(counterKey, 1);
        var reply = Set(["SET"u8.ToArray(), key, args[5], "NX"u8.ToArray(), "PX"u8.ToArray(), args[6]]);
        return reply.Prefix == '-' ? reply : FakeReply.Bulk(Find(counterKey)!.Value);
    }
}
