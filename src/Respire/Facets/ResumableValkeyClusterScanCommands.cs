using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

internal sealed partial class KeyCommands
{
    private ClusterScanCapabilityCache? _scanCapabilities;

    private ValueTask<bool> SupportsClusterScanAsync(RespireConnection connection, string runId,
        ScanRecovery recovery, CancellationToken cancellationToken)
    {
        var cache = _scanCapabilities;
        if (cache is null)
        {
            var created = new ClusterScanCapabilityCache();
            cache = Interlocked.CompareExchange(ref _scanCapabilities, created, null) ?? created;
        }
        return cache.SupportsAsync(client, connection, runId, recovery.Capabilities, cancellationToken, recovery.Observation);
    }

    private async ValueTask<RespireClusterScanPage?> ReadValkeyScanPageAsync(ClusterScanState state,
        ScanTopology topology, ScanNode node, string runId, string? match, RespireKeyType? type,
        int countHint, CancellationToken cancellationToken, ScanRecovery recovery)
    {
        var starting = state.ValkeyCursor is null;
        if (!ClusterScanPassCertifier.BeginOwnerRange(state, topology.Moving, node.Metadata.Id,
            node.Metadata.ConfigurationEpoch, runId, out var slot))
            return new(new RespireClusterScanCursor(state), []) { WaitingOnMigration = true };
        var command = CreateValkeyClusterScanCommand(client, state.ValkeyCursor ?? "0", match, type,
            countHint, starting ? slot : null, cancellationToken);
        var sent = await SendValkeyScanWithRedirectsAsync(state, topology, node, runId, command,
            slot, starting, cancellationToken, recovery).ConfigureAwait(false);
        if (sent.Unsupported)
            return sent.Redirected && !starting ? new(new RespireClusterScanCursor(state), []) : null;
        return await ApplyValkeyScanPageAsync(state, sent, slot, starting, match,
            cancellationToken, recovery.Discovery, recovery.Observation).ConfigureAwait(false);
    }

    private readonly record struct ValkeyScanReply(RespValue Reply, bool Redirected, bool Unsupported);

    private async ValueTask<ValkeyScanReply> SendValkeyScanWithRedirectsAsync(ClusterScanState state,
        ScanTopology topology, ScanNode node, string runId, ValkeyClusterScanCommand command,
        int slot, bool starting, CancellationToken cancellationToken, ScanRecovery recovery)
    {
        var connection = node.Connection;
        var redirected = false;
        var asking = false;
        while (true)
        {
            try
            {
                var reply = await client.SendOnPinnedConnectionAsync("CLUSTERSCAN", connection, command,
                    cancellationToken, observation: recovery.Observation, sendAsking: asking).ConfigureAwait(false);
                return new(reply, redirected, Unsupported: false);
            }
            catch (RespireServerException error) when (ClusterScanCommandErrors.IsUnknown(error, "CLUSTERSCAN"))
            {
                // This is evidence about the actual destination, never about the redirect source.
                recovery.Observation.Handled(error);
                _scanCapabilities!.RecordAbsent(connection, runId);
                state.ResetPass();
                return new(default, redirected, Unsupported: true);
            }
            catch (RespireServerException error) when (ClusterScanCommandErrors.IsDenied(error) && starting && !redirected)
            {
                // Execution ACLs may differ from metadata ACLs. Preserve the legacy
                // bootstrap path without claiming the command is absent.
                recovery.Observation.Handled(error);
                state.ResetPass();
                return new(default, redirected, Unsupported: true);
            }
            catch (RespireServerException error) when (client.Core.Cluster is { } cluster
                && cluster.CanRetryRetirement(recovery.Rejections, cancellationToken) && ClusterRouter.IsRedirect(error))
            {
                redirected = true;
                recovery.Observation.Handled(error);
                recovery.Rejections++;
                cluster.RecordRejection(ref recovery.Discovery, connection, error);
                if (ClusterRouter.TryParseRedirect(error, connection.Host, out var changedSlot, out _)
                    && (!starting || changedSlot == slot))
                {
                    state.Completed[changedSlot] = false;
                    state.PassSlots[changedSlot] = false;
                    // A rejected scan position is direct evidence of a transition even
                    // before metadata catches up. Literal-zero bootstrap redirects for
                    // another slot have scanned no data and do not invalidate its progress.
                    topology.Moving[changedSlot] = true;
                }
                connection = await cluster.GetRedirectConnectionAsync(error, connection,
                    cancellationToken, slot, recovery.Discovery).ConfigureAwait(false);
                asking = error.Code == RespireErrorCodes.Ask;
                runId = await ReadScanRunIdAsync(connection, cancellationToken, recovery.Observation).ConfigureAwait(false);
                if (!await SupportsClusterScanAsync(connection, runId, recovery, cancellationToken).ConfigureAwait(false))
                {
                    state.ResetPass();
                    return new(default, redirected, Unsupported: true);
                }
            }
        }
    }

    private async ValueTask<RespireClusterScanPage> ApplyValkeyScanPageAsync(ClusterScanState state,
        ValkeyScanReply sent, int slot, bool starting, string? match,
        CancellationToken cancellationToken, ClusterRouter.DiscoveryRound? discovery, RespireTelemetry.ErrorObservation observation)
    {
        var reply = sent.Reply;
        using (reply)
        {
            // Keep the page API's existing string decoding/prefix contract. The typed
            // primitive remains binary-safe for callers needing byte-preserving keys.
            var nextCursor = ValkeyClusterScanParser.ReadCursor(in reply);
            var raw = reply.AsArray()[1].AsArray();
            var keys = new List<string>(raw.Length);
            var prefix = client.EncodedKeyPrefix;
            var effectiveMatch = prefix is { HasSurrogateBoundary: true } ? null : ScanMatch(prefix, match);
            var pattern = prefix is { HasSurrogateBoundary: true } && match is not null
                ? Encoding.UTF8.GetBytes(match) : null;
            foreach (ref readonly var key in raw)
            {
                if (key.Type != RespDataType.BulkString || key.IsNull)
                    throw new RespireProtocolException("CLUSTERSCAN keys must be bulk strings.");
                if (state.Completed[ClusterHash.GetSlot(key.AsSpan())]) continue;
                if (prefix is null) keys.Add(key.AsString());
                else if (ScanKey(in key, prefix) is { } stripped
                    && (pattern is null || ByteGlob.IsMatch(Encoding.UTF8.GetBytes(stripped), pattern))) keys.Add(stripped);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (ClusterScanPassCertifier.ApplyPosition(state, nextCursor, slot, starting, sent.Redirected, raw.Length == 0))
                await CompleteScanPassAsync(state, effectiveMatch, cancellationToken, discovery, observation).ConfigureAwait(false);
            return new(new RespireClusterScanCursor(state), keys.ToArray());
        }
    }

}
