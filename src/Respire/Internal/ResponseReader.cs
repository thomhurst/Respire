using System.Buffers.Text;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>
/// Pure readers from a <see cref="RespValue"/> to plain .NET types. None of these dispose the
/// value — ownership stays with the caller, so the same readers serve both owned replies and
/// borrowed transaction-array elements (whose storage the parent reply owns).
/// </summary>
internal static class ResponseReader
{
    public static long Integer(in RespValue value) => value.AsInteger();

    /// <summary>An affirmative reply: RESP3 boolean true or integer &gt;= 1.</summary>
    public static bool Flag(in RespValue value)
        => value.Type == RespDataType.Boolean ? value.AsBoolean() : value.AsInteger() >= 1;

    /// <summary>+OK → true, null → false (conditional writes like SET NX).</summary>
    public static bool OkOrNull(in RespValue value) => !value.IsNull;

    /// <summary>+OK → true; anything else throws. For deferred results, whose readers must return a value.</summary>
    public static bool Ok(in RespValue value)
    {
        ExpectOk(in value);
        return true;
    }

    public static void ExpectOk(in RespValue value)
    {
        if (value.Type != RespDataType.SimpleString || !value.AsSpan().SequenceEqual("OK"u8))
        {
            throw new RespireException($"Expected 'OK' but got: {value}");
        }
    }

    public static string String(in RespValue value) => value.AsString();

    public static string? StringOrNull(in RespValue value) => value.IsNull ? null : value.AsString();

    public static byte[]? BytesOrNull(in RespValue value) => value.IsNull ? null : value.AsSpan().ToArray();

    public static double Double(in RespValue value)
    {
        if (value.Type == RespDataType.Double)
        {
            return value.AsDouble();
        }

        if (value.Type == RespDataType.Integer)
        {
            return value.AsInteger();
        }

        return Utf8Parser.TryParse(value.AsSpan(), out double parsed, out _) ? parsed : 0;
    }

    public static double? DoubleOrNull(in RespValue value) => value.IsNull ? null : Double(in value);

    public static long? IntegerOrNull(in RespValue value) => value.IsNull ? null : value.AsInteger();

    public static long? IntegerMinusOneOrNull(in RespValue value)
    {
        var result = value.AsInteger();
        return result == -1 ? null : result;
    }

    public static string[] StringArray(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length == 0)
        {
            return [];
        }

        var result = new string[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = elements[i].AsString();
        }

        return result;
    }

    public static string?[] NullableStringArray(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length == 0)
        {
            return [];
        }

        var result = new string?[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = elements[i].IsNull ? null : elements[i].AsString();
        }

        return result;
    }

    public static long?[] NullableIntegerArray(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length == 0)
        {
            return [];
        }

        var result = new long?[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = elements[i].IsNull ? null : elements[i].AsInteger();
        }

        return result;
    }

    public static HashFieldExpiry[] HashFieldExpiryArray(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length == 0)
        {
            return [];
        }

        var result = new HashFieldExpiry[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = HashFieldExpiry.FromHpttl(elements[i].AsInteger());
        }

        return result;
    }

    public static HashFieldExpiryResult[] HashFieldExpiryResultArray(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length == 0)
        {
            return [];
        }

        var result = new HashFieldExpiryResult[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = (HashFieldExpiryResult)elements[i].AsInteger();
        }

        return result;
    }

    /// <summary>Flattened key,value,key,value pairs (HGETALL, CONFIG GET) into a dictionary.</summary>
    public static Dictionary<string, string> StringMap(in RespValue value)
    {
        var elements = value.AsArray();
        var result = new Dictionary<string, string>(elements.Length / 2);
        for (var i = 0; i + 1 < elements.Length; i += 2)
        {
            result[elements[i].AsString()] = elements[i + 1].AsString();
        }

        return result;
    }

    public static RespireServerException ServerError(in RespValue value, string? commandName = null)
        => new(value.GetErrorMessage(), commandName);
}
