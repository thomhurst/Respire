using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private FakeReply IncrementExtended(byte[][] args)
    {
        var options = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var token = Token(args[index]);
            byte[] value;
            if (token is "SATURATE" or "PERSIST" or "ENX") value = [];
            else if (token is "BYINT" or "BYFLOAT" or "LBOUND" or "UBOUND" or "EX" or "PX" or "EXAT" or "PXAT"
                && index + 1 < args.Length) value = args[++index];
            else return Syntax("INCREX");
            if (!options.TryAdd(token, value)) return Syntax("INCREX");
        }
        if (options.ContainsKey("BYINT") && options.ContainsKey("BYFLOAT")) return Syntax("INCREX");
        string? expiration = null;
        long? expiresAt = null;
        foreach (var token in new[] { "EX", "PX", "EXAT", "PXAT", "PERSIST" })
        {
            if (!options.TryGetValue(token, out var amount)) continue;
            if (expiration is not null) return Syntax("INCREX");
            expiration = token;
            if (token == "PERSIST") continue;
            var parsed = Integer(amount);
            if (parsed <= 0) return FakeReply.Error("ERR invalid expire time in INCREX");
            expiresAt = Expiration(token, parsed);
        }
        var enx = options.ContainsKey("ENX");
        if (enx && expiration is null or "PERSIST") return Syntax("INCREX");
        var saturate = options.ContainsKey("SATURATE");
        // Validate arguments before looking up the key, including both bounds in the selected numeric mode.
        var floating = options.TryGetValue("BYFLOAT", out var floatBytes);
        var integerBy = options.TryGetValue("BYINT", out var intBytes) ? Integer(intBytes) : 1;
        var integerLower = !floating && options.TryGetValue("LBOUND", out var integerLowerBytes) ? Integer(integerLowerBytes) : long.MinValue;
        var integerUpper = !floating && options.TryGetValue("UBOUND", out var integerUpperBytes) ? Integer(integerUpperBytes) : long.MaxValue;
        var floatBy = floating ? IncrementFloat(floatBytes!) : 0;
        var floatLower = floating && options.TryGetValue("LBOUND", out var floatLowerBytes) ? IncrementFloat(floatLowerBytes, allowInfinity: true) : double.NegativeInfinity;
        var floatUpper = floating && options.TryGetValue("UBOUND", out var floatUpperBytes) ? IncrementFloat(floatUpperBytes, allowInfinity: true) : double.PositiveInfinity;
        if (integerLower > integerUpper || floatLower > floatUpper) return Syntax("INCREX");

        var entry = Find(args[1]);
        FakeReply reply;
        string stored;
        if (floating)
        {
            var current = entry is null ? 0 : IncrementFloat(entry.Value);
            // Redis's human-formatted floating replies and stored results canonicalize negative zero.
            if (current == 0) current = 0;
            var result = current + floatBy;
            // The fake uses double, not the server's platform-dependent long double arithmetic.
            if (!double.IsFinite(result)) return FakeReply.Error("ERR Respire.Testing INCREX arithmetic exceeds double range");
            if (result < floatLower || result > floatUpper)
            {
                if (!saturate) return FakeReply.Array([FakeReply.Double(current), FakeReply.Double(0)]);
                result = Math.Clamp(result, floatLower, floatUpper);
            }
            if (result == 0) result = 0;
            var delta = result - current;
            if (delta == 0) delta = 0;
            if (!double.IsFinite(delta)) return FakeReply.Error("ERR Respire.Testing INCREX applied increment exceeds double range");
            stored = result.ToString("R", CultureInfo.InvariantCulture);
            reply = FakeReply.Array([FakeReply.Double(result), FakeReply.Double(delta)]);
        }
        else
        {
            var current = entry is null ? 0 : Integer(entry.Value);
            var sum = (Int128)current + integerBy;
            if (sum < integerLower || sum > integerUpper)
            {
                if (!saturate) return FakeReply.Array([FakeReply.Integer(current), FakeReply.Integer(0)]);
                sum = Int128.Clamp(sum, integerLower, integerUpper);
            }
            var result = (long)sum;
            var delta = checked(result - current);
            stored = result.ToString(CultureInfo.InvariantCulture);
            reply = FakeReply.Array([FakeReply.Integer(result), FakeReply.Integer(delta)]);
        }

        var applyExpiry = expiresAt.HasValue && (!enx || entry?.ExpiresAt is null);
        if (applyExpiry && expiresAt <= Now) DeleteEntry(args[1]);
        else SetEntry(args[1], new Entry(Encoding.ASCII.GetBytes(stored),
            expiration == "PERSIST" ? null : applyExpiry ? expiresAt : entry?.ExpiresAt));
        return reply;
    }

    private static double IncrementFloat(byte[] bytes, bool allowInfinity = false)
    {
        if (!RedisScore.TryParse(bytes, out var value) || !allowInfinity && !double.IsFinite(value))
            throw new FormatException("INCREX requires a valid number; only bounds may be infinite.");
        return value;
    }
}
