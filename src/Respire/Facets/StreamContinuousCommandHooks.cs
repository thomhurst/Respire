using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

internal sealed partial class StreamCommands
{
    // Only the configured path allocates page state. The original iterator remains the
    // hook-absent path, including its incremental validation and pooled dispatch owner.
    private async IAsyncEnumerable<RespireStreamReadEntry> ReadContinuouslyHookedAsync(
        (RespireKey Key, RespireStreamId After)[] initial, int batchSize, RespireCommandHook hook,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<bool>.Start();
        Exception? failure = null;
        try
        {
            var page = new ContinuousHookPage(initial.ToArray(), batchSize, cancellationToken, owner.Observation);
            while (true)
            {
                RespireStreamReadResult[] results;
                try
                {
                    // Admission surrounds resolution and recovery, so a transport-shaped
                    // policy exception can never enter the retry loop.
                    results = await CommandHookScope.RunAsync(hook, "XREAD", cancellationToken,
                        (Owner: this, Page: page), static s => s.Owner.ReadHookPageAsync(s.Page)).ConfigureAwait(false);
                }
                catch (Exception error) { failure = error; throw; }
                foreach (var result in results)
                {
                    foreach (var entry in result.Entries)
                    {
                        try { cancellationToken.ThrowIfCancellationRequested(); }
                        catch (Exception error) { failure = error; throw; }
                        yield return new RespireStreamReadEntry(result.Key, entry);
                    }
                }
            }
        }
        finally { FinishReadIterator(owner, failure); }
    }

    private sealed class ContinuousHookPage((RespireKey Key, RespireStreamId After)[] streams,
        int batchSize, CancellationToken token, RespireTelemetry.ErrorObservation observation)
    {
        internal readonly (RespireKey Key, RespireStreamId After)[] Streams = streams;
        internal readonly Dictionary<RespireKey, int> Indexes = streams.Select((stream, index) => (stream.Key, index))
            .ToDictionary(x => x.Key, x => x.index);
        internal readonly int BatchSize = batchSize;
        internal readonly CancellationToken Token = token;
        internal readonly RespireTelemetry.ErrorObservation Observation = observation;
        internal StreamReadCommand? Command;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireStreamReadResult[]> ReadHookPageAsync(ContinuousHookPage page)
    {
        // Resolve $ once per stream, inside the first XREAD page. Resolution failures
        // remain terminal, just as on the hook-absent path.
        if (page.Command is null)
        {
            for (var i = 0; i < page.Streams.Length; i++)
            {
                if (page.Streams[i].After != RespireStreamId.New) continue;
                var newest = await client.ConvertUnobservedResponseAsync("XREVRANGE",
                    new Cmd5(Verbs.XRevRange, client.Key(page.Streams[i].Key), "+", "-", "COUNT", 1),
                    page.Token, this,
                    static (StreamCommands _, in RespValue reply) => ParseEntries(in reply, null, default, null),
                    page.Observation).ConfigureAwait(false);
                page.Streams[i].After = newest.Length == 0 ? RespireStreamId.Beginning : newest[0].Id;
            }
            page.Command = BuildReadCommand(client, page.Streams, page.BatchSize, TimeSpan.FromSeconds(1));
        }

        var command = page.Command.Value;
        var failures = 0;
        RespireStreamReadResult[] results;
        while (true)
        {
            try
            {
                page.Token.ThrowIfCancellationRequested();
                results = await client.ConvertUnobservedResponseAsync("XREAD", command, page.Token, client,
                    static (RespireClient owner, in RespValue reply) => ParseStreamRead(in reply, owner),
                    page.Observation, blocking: true).ConfigureAwait(false);
                break;
            }
            catch (Exception error) when (!page.Token.IsCancellationRequested && !client.Core.Disposed && CanRetryStreamRead(error))
            {
                page.Observation.Handled(error);
                var delayMilliseconds = 100 * (1 << Math.Min(failures, 5));
                failures = Math.Min(failures + 1, 6);
                client.Core.Logger?.StreamReadRetry(delayMilliseconds, error);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), page.Token).ConfigureAwait(false);
            }
        }

        // Validate the whole owned page before successful completion or publishing its
        // first entry. No next page is fetched until all these entries are delivered.
        foreach (var result in results)
        {
            if (!page.Indexes.TryGetValue(result.Key, out var index))
                throw new RespireProtocolException("XREAD returned an unrequested stream.");
            foreach (var entry in result.Entries)
            {
                if (entry.Id <= page.Streams[index].After)
                    throw new RespireProtocolException("XREAD returned a non-increasing entry id.");
                page.Streams[index].After = entry.Id;
                command.SetAfter(index, entry.Id);
            }
        }
        page.Command = command;
        return results;
    }
}
