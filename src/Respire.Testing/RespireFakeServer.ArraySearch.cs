using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private FakeReply ArrayOperation(byte[][] args)
    {
        var start = ArrayIndex(args[2]);
        var end = ArrayIndex(args[3]);
        var operation = Token(args[4]);
        if (operation is not ("SUM" or "MIN" or "MAX" or "AND" or "OR" or "XOR" or "MATCH" or "USED"))
            return FakeReply.Error("ERR unknown operation");
        if (args.Length != (operation == "MATCH" ? 6 : 5)) return WrongArity("AROP");
        var values = ArrayItems(FindArray(args[1]), Math.Min(start, end), Math.Max(start, end));
        if (operation == "USED") return FakeReply.Integer(values.LongCount());
        if (operation == "MATCH") return FakeReply.Integer(values.LongCount(item => item.Value.AsSpan().SequenceEqual(args[5])));
        decimal? number = null;
        long? bits = null;
        foreach (var item in values)
        {
            var text = Encoding.ASCII.GetString(item.Value);
            // Decimal arithmetic is deliberately bounded. Do not round unsupported
            // numeric inputs and pretend to reproduce Redis long-double arithmetic.
            if (text.Length == 0 || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])) continue;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return FakeReply.Error("ERR Respire.Testing does not support this AROP numeric range");
                continue;
            }
            if (operation is "AND" or "OR" or "XOR")
            {
                parsed = decimal.Truncate(parsed);
                if (parsed < long.MinValue || parsed > long.MaxValue) continue;
                var integer = (long)parsed;
                bits = bits is not { } previous ? integer : operation switch
                {
                    "AND" => previous & integer, "OR" => previous | integer, _ => previous ^ integer,
                };
            }
            else
            {
                try
                {
                    number = number is not { } previous ? parsed : operation switch
                    {
                        "SUM" => previous + parsed, "MIN" => Math.Min(previous, parsed), _ => Math.Max(previous, parsed),
                    };
                }
                catch (OverflowException) { return FakeReply.Error("ERR Respire.Testing does not support this AROP numeric range"); }
            }
        }
        if (operation is "AND" or "OR" or "XOR") return bits is { } result ? FakeReply.Integer(result) : FakeReply.Null;
        return number is { } numeric ? FakeReply.Text(numeric.ToString("G29", CultureInfo.InvariantCulture)) : FakeReply.Null;
    }

    private sealed record ArrayPredicate(string Kind, byte[] Pattern, Regex? Expression);

    private FakeReply ArrayGrep(byte[][] args)
    {
        // Validate numeric bounds even when the key does not exist.
        static ulong Bound(byte[] value, ulong last) => Token(value) switch
        {
            "-" => 0, "+" => last, _ => ArrayIndex(value),
        };
        _ = Bound(args[2], 0);
        _ = Bound(args[3], 0);
        List<(string Kind, byte[] Pattern)> requested = [];
        var all = false;
        var ignoreCase = false;
        var withValues = false;
        var limit = long.MaxValue;
        for (var i = 4; i < args.Length; i++)
        {
            var token = Token(args[i]);
            if (token is "EXACT" or "MATCH" or "GLOB" or "RE")
            {
                if (++i >= args.Length || requested.Count == 250) return Syntax("ARGREP");
                requested.Add((token, args[i]));
            }
            else if (token == "AND") all = true;
            else if (token == "OR") all = false;
            else if (token == "NOCASE") ignoreCase = true;
            else if (token == "WITHVALUES") withValues = true;
            else if (token == "LIMIT")
            {
                if (++i >= args.Length) return Syntax("ARGREP");
                limit = Integer(args[i]);
                if (limit <= 0) return FakeReply.Error("ERR LIMIT must be positive");
            }
            else return Syntax("ARGREP");
        }
        if (requested.Count == 0) return Syntax("ARGREP");
        List<ArrayPredicate> predicates = [];
        foreach (var (kind, pattern) in requested)
        {
            Regex? expression = null;
            if (kind == "RE")
            {
                // Redis uses POSIX ERE; the fake supports only the common ASCII subset.
                // Reject dialect-specific syntax instead of silently using .NET semantics.
                var text = Encoding.Latin1.GetString(pattern);
                if (pattern.Length > 2048 || pattern.Any(value => value is 0 or > 127)
                    || text.Contains("(?", StringComparison.Ordinal) || text.Contains("[:", StringComparison.Ordinal)
                    || text.Contains("[.", StringComparison.Ordinal) || text.Contains("[=", StringComparison.Ordinal)
                    || text.Contains('\\'))
                    return FakeReply.Error("ERR Respire.Testing supports only ASCII ARGREP RE without escapes or POSIX classes");
                try
                {
                    expression = new Regex(text, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException) { return FakeReply.Error("ERR invalid regular expression"); }
            }
            predicates.Add(new(kind, pattern, expression));
        }
        var array = FindArray(args[1]);
        if (array is null) return FakeReply.Array([]);
        var start = Bound(args[2], array.Length - 1);
        var end = Bound(args[3], array.Length - 1);
        List<FakeReply> result = [];
        foreach (var item in ArrayItems(array, start, end))
        {
            if (predicates.Any(predicate => predicate.Kind == "RE") && item.Value.Any(value => value is < 32 or > 126))
                return FakeReply.Error("ERR Respire.Testing ARGREP RE supports only printable ASCII values");
            bool Match(ArrayPredicate predicate) => predicate.Kind switch
            {
                "EXACT" => ArrayEqual(item.Value, predicate.Pattern, ignoreCase),
                "MATCH" => ArrayContains(item.Value, predicate.Pattern, ignoreCase),
                "GLOB" => ArrayGlob(item.Value, predicate.Pattern, ignoreCase),
                _ => predicate.Expression!.IsMatch(Encoding.Latin1.GetString(item.Value)),
            };
            bool matched;
            try { matched = all ? predicates.All(Match) : predicates.Any(Match); }
            catch (RegexMatchTimeoutException) { return FakeReply.Error("ERR Respire.Testing ARGREP regex exceeded its time limit"); }
            if (!matched) continue;
            result.Add(withValues ? FakeReply.Array([ArrayUnsigned(item.Key), FakeReply.Bulk(item.Value)]) : ArrayUnsigned(item.Key));
            if (--limit == 0) break;
        }
        return FakeReply.Array(result.ToArray());
    }

    private static byte ArrayFold(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static bool ArrayEqual(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, bool ignoreCase)
    {
        if (value.Length != pattern.Length) return false;
        for (var i = 0; i < value.Length; i++)
            if ((ignoreCase ? ArrayFold(value[i]) : value[i]) != (ignoreCase ? ArrayFold(pattern[i]) : pattern[i])) return false;
        return true;
    }

    private static bool ArrayContains(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, bool ignoreCase)
    {
        for (var i = 0; i <= value.Length - pattern.Length; i++)
            if (ArrayEqual(value.Slice(i, pattern.Length), pattern, ignoreCase)) return true;
        return false;
    }

    private static bool ArrayGlob(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, bool ignoreCase)
    {
        // Redis glob syntax is byte based, including bracket ranges and escaped literals.
        while (!pattern.IsEmpty)
        {
            var token = pattern[0];
            pattern = pattern[1..];
            if (token == '*')
            {
                while (!pattern.IsEmpty && pattern[0] == '*') pattern = pattern[1..];
                if (pattern.IsEmpty) return true;
                for (var i = 0; i <= value.Length; i++) if (ArrayGlob(value[i..], pattern, ignoreCase)) return true;
                return false;
            }
            if (value.IsEmpty) return false;
            var actual = ignoreCase ? ArrayFold(value[0]) : value[0];
            if (token == '[')
            {
                var negate = !pattern.IsEmpty && pattern[0] == '^';
                if (negate) pattern = pattern[1..];
                var matched = false;
                while (!pattern.IsEmpty && pattern[0] != ']')
                {
                    var first = pattern[0];
                    pattern = pattern[1..];
                    if (first == '\\' && !pattern.IsEmpty) { first = pattern[0]; pattern = pattern[1..]; }
                    if (ignoreCase) first = ArrayFold(first);
                    if (pattern.Length >= 2 && pattern[0] == '-')
                    {
                        var last = ignoreCase ? ArrayFold(pattern[1]) : pattern[1];
                        matched |= actual >= Math.Min(first, last) && actual <= Math.Max(first, last);
                        pattern = pattern[2..];
                    }
                    else matched |= actual == first;
                }
                if (!pattern.IsEmpty) pattern = pattern[1..];
                if (matched == negate) return false;
            }
            else if (token != '?')
            {
                if (token == '\\' && !pattern.IsEmpty) { token = pattern[0]; pattern = pattern[1..]; }
                if ((ignoreCase ? ArrayFold(token) : token) != actual) return false;
            }
            value = value[1..];
        }
        return value.IsEmpty;
    }
}
