using System.Globalization;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Protocol;

namespace Respire.Internal;

internal delegate T[] ScanPageParser<T>(in RespValue page);

internal static class CollectionScan
{
    internal static async IAsyncEnumerable<T> EnumerateAsync<T>(
        RespireClient client,
        string operation,
        Verb verb,
        RespireKey key,
        string? match,
        int countHint,
        ScanPageParser<T> parsePage,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool noValues = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);

        var wireKey = client.Key(in key);
        ulong cursor = 0;
        // Every page of this enumeration returns to the server that issued its cursor.
        var affinity = new ReadAffinity();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = Arguments(wireKey, cursor, match, countHint, noValues);
            var result = await client.ConvertCursorPageAsync(operation, new CmdN(verb, args), affinity,
                cancellationToken, (Operation: operation, Parser: parsePage),
                static ((string Operation, ScanPageParser<T> Parser) state, in RespValue reply) =>
                {
                    var elements = ParsePage(in reply, state.Operation, out var nextCursor);
                    return (Cursor: nextCursor, Page: state.Parser(in elements[1]));
                }).ConfigureAwait(false);
            cursor = result.Cursor;
            var page = result.Page;

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
        while (cursor != 0);
    }

    internal static ReadOnlySpan<RespValue> ParsePage(in RespValue reply, string operation, out ulong cursor)
    {
        if (reply.Type != RespDataType.Array)
            throw new RespireProtocolException($"{operation} must return an unsigned cursor and an item array.");
        var parts = reply.AsArray();
        if (parts.Length != 2
            || !ulong.TryParse(parts[0].AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out cursor))
            throw new RespireProtocolException($"{operation} must return an unsigned cursor and an item array.");
        return parts;
    }

    internal static RespireValue[] Arguments(
        RespireValue key, RespireValue cursor, string? match, int? countHint, bool noValues)
    {
        if (countHint is { } count) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count, nameof(countHint));
        var args = new RespireValue[2 + (match is null ? 0 : 2) + (countHint.HasValue ? 2 : 0) + (noValues ? 1 : 0)];
        args[0] = key;
        args[1] = cursor;
        var index = 2;
        if (match is not null)
        {
            args[index++] = "MATCH";
            args[index++] = match;
        }
        if (countHint.HasValue)
        {
            args[index++] = "COUNT";
            args[index++] = countHint.Value;
        }
        if (noValues) args[index] = "NOVALUES";
        return args;
    }
}
