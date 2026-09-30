using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private static FakeReply Syntax(string command) => FakeReply.Error($"ERR unsupported or invalid {command} arguments");

    private FakeReply Set(byte[][] args)
    {
        if (args.Length < 3) return Syntax("SET");
        var nx = false;
        var xx = false;
        var get = false;
        var keepTtl = false;
        string? expiration = null;
        long? expiresAt = null;
        for (var index = 3; index < args.Length; index++)
        {
            var option = Token(args[index]);
            switch (option)
            {
                case "NX" when !xx: nx = true; break;
                case "XX" when !nx: xx = true; break;
                case "GET": get = true; break;
                case "KEEPTTL" when expiration is null: keepTtl = true; break;
                case "EX" or "PX" or "EXAT" or "PXAT"
                    when !keepTtl && (expiration is null || expiration == option) && index + 1 < args.Length:
                    expiration = option;
                    var amount = Integer(args[++index]);
                    if (amount <= 0) return FakeReply.Error("ERR invalid expire time in SET");
                    expiresAt = Expiration(option, amount);
                    break;
                default: return Syntax("SET");
            }
        }
        var old = Find(args[1]);
        var previous = get ? old?.Value : null; // SET GET checks type even when NX rejects the write.
        if ((nx && old is not null) || (xx && old is null)) return get ? FakeReply.Bulk(previous) : FakeReply.Null;
        _entries[args[1]] = new Entry(args[2], keepTtl ? old?.ExpiresAt : expiresAt);
        return get ? FakeReply.Bulk(previous) : FakeReply.Ok;
    }

    private FakeReply MultiSet(string command, byte[][] args)
    {
        if (args.Length % 2 == 0) return WrongArity(command);
        if (command == "MSETNX")
            for (var index = 1; index < args.Length; index += 2)
                if (Find(args[index]) is not null) return FakeReply.Integer(0);
        for (var index = 1; index < args.Length; index += 2)
            _entries[args[index]] = new Entry(args[index + 1]);
        return command == "MSETNX" ? FakeReply.Integer(1) : FakeReply.Ok;
    }

    private FakeReply GetDelete(byte[] key)
    {
        var old = Find(key)?.Value;
        _entries.Remove(key);
        return FakeReply.Bulk(old);
    }

    private FakeReply GetSet(byte[] key, byte[] value)
    {
        var old = Find(key)?.Value;
        _entries[key] = new Entry(value);
        return FakeReply.Bulk(old);
    }

    private FakeReply Append(byte[] key, byte[] value)
    {
        var old = Find(key);
        byte[] combined = [.. old?.Value ?? [], .. value];
        _entries[key] = new Entry(combined, old?.ExpiresAt);
        return FakeReply.Integer(combined.Length);
    }

    private FakeReply Increment(byte[] key, long amount, bool subtract = false)
    {
        var old = Find(key);
        var current = old is null ? 0 : Integer(old.Value);
        var result = subtract ? checked(current - amount) : checked(current + amount);
        _entries[key] = new Entry(Encoding.ASCII.GetBytes(result.ToString(CultureInfo.InvariantCulture)), old?.ExpiresAt);
        return FakeReply.Integer(result);
    }

    private long Expiration(string option, long amount)
    {
        var milliseconds = option is "EX" or "EXAT" or "EXPIRE" or "EXPIREAT" ? checked(amount * 1000) : amount;
        return option.EndsWith("AT", StringComparison.Ordinal) ? milliseconds : checked(Now + milliseconds);
    }

    private FakeReply Expire(string command, byte[][] args)
    {
        if (args.Length < 3) return Syntax(command);
        var expires = Expiration(command, Integer(args[2]));
        var options = args.Skip(3).Select(Token).ToHashSet(StringComparer.Ordinal);
        if (options.Any(option => option is not ("NX" or "XX" or "GT" or "LT")) ||
            options.Contains("NX") && options.Count > 1 || options.Contains("GT") && options.Contains("LT")) return Syntax(command);
        var entry = Find(args[1]);
        if (entry is null || options.Contains("NX") && entry.ExpiresAt is not null ||
            options.Contains("XX") && entry.ExpiresAt is null ||
            options.Contains("GT") && (entry.ExpiresAt is null || expires <= entry.ExpiresAt) ||
            options.Contains("LT") && entry.ExpiresAt is { } old && expires >= old) return FakeReply.Integer(0);
        entry.ExpiresAt = expires;
        if (expires <= Now) _entries.Remove(args[1]);
        return FakeReply.Integer(1);
    }

    private FakeReply TimeToLive(string command, byte[] key)
    {
        var entry = Find(key);
        if (entry is null) return FakeReply.Integer(-2);
        if (entry.ExpiresAt is not { } expires) return FakeReply.Integer(-1);
        var value = command is "EXPIRETIME" or "PEXPIRETIME" ? expires : expires - Now;
        if (command is "TTL" or "EXPIRETIME") value = value / 1000 + (value % 1000 >= 500 ? 1 : 0);
        return FakeReply.Integer(value);
    }

    private FakeReply Persist(byte[] key)
    {
        var entry = Find(key);
        if (entry?.ExpiresAt is null) return FakeReply.Integer(0);
        entry.ExpiresAt = null;
        return FakeReply.Integer(1);
    }

    private FakeReply GetExpire(byte[][] args)
    {
        if (args.Length < 2) return Syntax("GETEX");
        var persist = false;
        string? expiration = null;
        long? expires = null;
        for (var index = 2; index < args.Length; index++)
        {
            var option = Token(args[index]);
            if (option == "PERSIST" && expiration is null) persist = true;
            else if (option is "EX" or "PX" or "EXAT" or "PXAT" && !persist &&
                     (expiration is null || expiration == option) && index + 1 < args.Length)
            {
                expiration = option;
                var amount = Integer(args[++index]);
                if (amount <= 0) return FakeReply.Error("ERR invalid expire time in GETEX");
                expires = Expiration(option, amount);
            }
            else return Syntax("GETEX");
        }
        var entry = Find(args[1]);
        if (entry is null) return FakeReply.Null;
        var value = entry.Value; // Validate type before changing the key's expiry.
        if (persist || expiration is not null) entry.ExpiresAt = expires;
        return FakeReply.Bulk(value);
    }
}
