using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire.Internal;

internal static class ArrayResponseReader
{
    // Redis sends values beyond Int64.MaxValue as bulk strings rather than RESP integers.
    internal static ulong Unsigned(in RespValue value)
    {
        if (value.Type == RespDataType.Integer && value.AsInteger() >= 0) return (ulong)value.AsInteger();
        var bytes = value.AsSpan();
        if (Utf8Parser.TryParse(bytes, out ulong result, out var consumed) && consumed == bytes.Length) return result;
        throw new RespireProtocolException("Expected an unsigned array integer.");
    }

    internal static ulong? UnsignedOrNull(in RespValue value) => value.IsNull ? null : Unsigned(in value);

    internal static ulong[] Indexes(in RespValue value)
    {
        var elements = value.AsArray();
        var result = new ulong[elements.Length];
        for (var i = 0; i < elements.Length; i++) result[i] = Unsigned(in elements[i]);
        return result;
    }

    internal static RespireArrayAggregate Aggregate(in RespValue value)
        => value.IsNull ? default : value.Type == RespDataType.Integer
            ? new(null, value.AsInteger()) : new(value.AsString(), null);

    internal static RespireArrayEntry<string>[] Entries(in RespValue value)
    {
        var elements = value.AsArray();
        var result = new RespireArrayEntry<string>[elements.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var pair = EntryPair(in elements[i]);
            result[i] = new(Unsigned(in pair[0]), pair[1].AsString());
        }
        return result;
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal static RespireArrayEntry<T>[] Entries<T>(RespireClient client, in RespValue value)
    {
        var elements = value.AsArray();
        var result = new RespireArrayEntry<T>[elements.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var pair = EntryPair(in elements[i]);
            result[i] = new(Unsigned(in pair[0]), client.DeserializeBorrowed<T>(in pair[1]));
        }
        return result;
    }

    private static ReadOnlySpan<RespValue> EntryPair(in RespValue value)
    {
        var pair = value.AsArray();
        if (pair.Length != 2) throw new RespireProtocolException("Expected an index/value pair.");
        return pair;
    }

    internal static RespireArrayInfo Info(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length % 2 != 0) throw new RespireProtocolException("Expected ARINFO key/value pairs.");
        var result = new RespireArrayInfo();
        for (var i = 0; i < elements.Length; i += 2)
        {
            ref readonly var field = ref elements[i + 1];
            switch (elements[i].AsString())
            {
                case "count": result.Count = Unsigned(in field); break;
                case "len": result.Length = Unsigned(in field); break;
                case "next-insert-index": result.NextInsertIndex = Unsigned(in field); break;
                case "slices": result.Slices = Unsigned(in field); break;
                case "directory-size": result.DirectorySize = Unsigned(in field); break;
                case "super-dir-entries": result.SuperDirectoryEntries = Unsigned(in field); break;
                case "slice-size": result.SliceSize = Unsigned(in field); break;
                case "dense-slices": result.DenseSlices = Unsigned(in field); break;
                case "sparse-slices": result.SparseSlices = Unsigned(in field); break;
                case "avg-dense-size": result.AverageDenseSize = ResponseReader.Double(in field); break;
                case "avg-dense-fill": result.AverageDenseFill = ResponseReader.Double(in field); break;
                case "avg-sparse-size": result.AverageSparseSize = ResponseReader.Double(in field); break;
            }
        }
        return result;
    }
}
