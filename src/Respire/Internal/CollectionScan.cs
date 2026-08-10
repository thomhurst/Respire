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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);

        var wireKey = client.Key(in key);
        var cursor = "0";
        do
        {
            var args = match is null
                ? new RespireValue[] { wireKey, cursor, "COUNT", countHint }
                : new RespireValue[] { wireKey, cursor, "MATCH", match, "COUNT", countHint };
            var reply = await client.SendAsync(operation, new CmdN(verb, args), cancellationToken)
                .ConfigureAwait(false);

            T[] page;
            try
            {
                var elements = reply.AsArray();
                cursor = elements[0].AsString();
                page = parsePage(in elements[1]);
            }
            finally
            {
                reply.Dispose();
            }

            foreach (var item in page)
            {
                yield return item;
            }
        }
        while (cursor != "0");
    }
}
