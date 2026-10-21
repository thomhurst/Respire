using Respire.Protocol;

namespace Respire.Internal;

internal static class PathHashCodec
{
    internal static RespireValue[] Set(RespireKey path, ReadOnlySpan<(RespireKey Field, RespireValue Value)> fields, RespireSetWhen when)
    {
        if (when is not (RespireSetWhen.Always or RespireSetWhen.NotExists or RespireSetWhen.Exists))
            throw new ArgumentOutOfRangeException(nameof(when));
        RequireFields(fields.Length);
        var args = new RespireValue[3 + fields.Length * 2 + (when == RespireSetWhen.Always ? 0 : 1)];
        var index = 0;
        args[index++] = path.AsValue();
        if (when != RespireSetWhen.Always) args[index++] = when == RespireSetWhen.NotExists ? "FNX" : "FXX";
        args[index++] = "FIELDS";
        args[index++] = fields.Length;
        foreach (var field in fields) { args[index++] = field.Field.AsValue(); args[index++] = field.Value; }
        return args;
    }

    internal static RespireValue[] SetMany(ReadOnlySpan<RespirePathHashWrite> paths)
    {
        RequireFields(paths.Length);
        var args = new List<RespireValue>();
        foreach (var path in paths)
        {
            ArgumentNullException.ThrowIfNull(path.Fields);
            args.AddRange(Set(path.Path, path.Fields, RespireSetWhen.Always));
        }
        return args.ToArray();
    }

    internal static RespireValue[] Get(RespireKey path, ReadOnlySpan<RespireKey> fields, bool allowEmpty = false)
    {
        if (!allowEmpty) RequireFields(fields.Length);
        var args = new RespireValue[1 + fields.Length];
        args[0] = path.AsValue();
        for (var i = 0; i < fields.Length; i++) args[i + 1] = fields[i].AsValue();
        return args;
    }

    internal static RespireValue[] GetMany(ReadOnlySpan<RespirePathHashRead> paths)
    {
        RequireFields(paths.Length);
        var args = new List<RespireValue>();
        foreach (var path in paths)
        {
            ArgumentNullException.ThrowIfNull(path.Fields);
            RequireFields(path.Fields.Length);
            args.Add(path.Path.AsValue()); args.Add("FIELDS"); args.Add(path.Fields.Length);
            foreach (var field in path.Fields) args.Add(field.AsValue());
        }
        return args.ToArray();
    }

    internal static RespireValue[] Prefixes(RespireKey query, long? count, long? maxPathLength)
    {
        if (count is <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (maxPathLength is < 0) throw new ArgumentOutOfRangeException(nameof(maxPathLength));
        var args = new List<RespireValue> { query.AsValue(), "WITHVALUES" };
        if (count.HasValue) { args.Add("COUNT"); args.Add(count.Value); }
        if (maxPathLength.HasValue) { args.Add("MAXPATHLEN"); args.Add(maxPathLength.Value); }
        return args.ToArray();
    }

    internal static RespireValue[] Scan(string cursor, RespireKey? prefix, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var args = new List<RespireValue> { cursor };
        if (prefix.HasValue) { args.Add("PREFIX"); args.Add(prefix.Value.AsValue()); }
        args.Add("COUNT"); args.Add(count); args.Add("WITHVALUES");
        return args.ToArray();
    }

    internal static bool Applied(in RespValue value)
        => !value.IsNull && ResponseReader.Ok(in value);

    internal static RespirePathHashField[] Fields(in RespValue value)
    {
        if (value.IsNull || value.Type is not (RespireDataType.Array or RespireDataType.Map)) throw Malformed();
        var pairs = value.AsArray();
        if (pairs.Length % 2 != 0) throw Malformed();
        var result = new RespirePathHashField[pairs.Length / 2];
        for (var i = 0; i < result.Length; i++) result[i] = new(Bytes(in pairs[i * 2]), Bytes(in pairs[i * 2 + 1]));
        return result;
    }

    internal static byte[]?[] Values(in RespValue value)
    {
        if (value.Type != RespireDataType.Array || value.IsNull) throw Malformed();
        var values = value.AsArray();
        var result = new byte[]?[values.Length];
        for (var i = 0; i < result.Length; i++) result[i] = values[i].IsNull ? null : Bytes(in values[i]);
        return result;
    }

    internal static RespValue Single(in RespValue value)
    {
        if (value.Type != RespireDataType.Array || value.IsNull || value.AsArray().Length != 1) throw Malformed();
        var item = value.AsArray()[0];
        if (!item.IsNull && item.Type is not (RespireDataType.BulkString or RespireDataType.SimpleString)) throw Malformed();
        return item;
    }

    internal static RespirePathHashEntry? Match(in RespValue value)
    {
        if (value.IsNull) return null;
        return Entry(in value);
    }

    internal static RespirePathHashEntry[] Entries(in RespValue value)
    {
        if (value.Type != RespireDataType.Array || value.IsNull) throw Malformed();
        var values = value.AsArray();
        var result = new RespirePathHashEntry[values.Length];
        for (var i = 0; i < result.Length; i++) result[i] = Entry(in values[i]);
        return result;
    }

    internal static RespirePathHashPage Page(in RespValue value)
    {
        if (value.Type != RespireDataType.Array || value.IsNull || value.AsArray().Length != 2) throw Malformed();
        var parts = value.AsArray();
        // Cursors encode server state; they are never parsed as SCAN's numeric cursor.
        if (parts[0].Type != RespireDataType.BulkString || parts[0].IsNull || parts[0].AsSpan().IsEmpty) throw Malformed();
        return new(parts[0].AsString(), Entries(in parts[1]));
    }

    private static RespirePathHashEntry Entry(in RespValue value)
    {
        if (value.Type != RespireDataType.Array || value.IsNull || value.AsArray().Length != 2) throw Malformed();
        var parts = value.AsArray();
        return new(Bytes(in parts[0]), Fields(in parts[1]));
    }

    private static byte[] Bytes(in RespValue value)
    {
        if (value.IsNull || value.Type is not (RespireDataType.BulkString or RespireDataType.SimpleString)) throw Malformed();
        return value.AsSpan().ToArray();
    }

    private static void RequireFields(int count)
    {
        if (count == 0) throw new ArgumentException("At least one path or field is required.");
    }

    private static RespireProtocolException Malformed() => new("Malformed Path Hash response.");
}
