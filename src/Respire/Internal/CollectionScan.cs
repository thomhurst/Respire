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
            var reply = await client.SendCursorPageAsync(operation, new CmdN(verb, args), affinity, cancellationToken)
                .ConfigureAwait(false);

            T[] page;
            try
            {
                cursor = ParseCursor(in reply, operation);
                var elements = reply.AsArray();
                page = parsePage(in elements[1]);
            }
            finally
            {
                reply.Dispose();
            }

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
        while (cursor != 0);
    }

    internal static ulong ParseCursor(in RespValue reply, string operation)
    {
        var parts = reply.AsArray();
        if (reply.Type != RespDataType.Array || parts.Length != 2
            || !ulong.TryParse(parts[0].AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
            throw new RespireProtocolException($"{operation} must return an unsigned cursor and an item array.");
        return cursor;
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
