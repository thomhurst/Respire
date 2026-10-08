using System.Globalization;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

internal sealed partial class KeyCommands
{
    private sealed class ScanRecovery
    {
        internal int Rejections;
        internal ClusterRouter.DiscoveryRound? Discovery;
        internal ClusterScanCapabilityCache.ProbeRound Capabilities { get; } = new();
        internal RespireTelemetry.ErrorObservation Observation { get; } = RespireTelemetry.ErrorObservation.Rent(force: true);
    }

    public async ValueTask<RespireClusterScanPage> ScanClusterPageAsync(
        RespireClusterScanCursor cursor, string? match = null, RespireKeyType? type = null,
        int countHint = 250, CancellationToken cancellationToken = default)
    {
        var recovery = new ScanRecovery();
        using var observation = recovery.Observation;
        try
        {
            while (true)
            {
                try
                {
                    return await ScanClusterPageCoreAsync(cursor, match, type, countHint, cancellationToken, recovery).ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException retirement) when (client.Core.Cluster is { } cluster
                    && cluster.CanRetryRetirement(recovery.Rejections, cancellationToken))
                {
                    // Rebuild from the immutable checkpoint, retaining the same fallback budget.
                    observation.Handled(retirement);
                    recovery.Rejections++;
                    cluster.RecordRejection(ref recovery.Discovery, retirement.Endpoint, retirement);
                }
            }
        }
        catch (Exception error)
        {
            // Cancellation before the next selection still ends a pending recovery. Errors
            // from INFO/SCAN after successful selection do not invalidate discovery telemetry.
            if (recovery.Discovery is { } discovery)
                discovery.RecordCommandFailure(error, discovery.HasPendingFailure, cancellationToken);
            observation.Final(error);
            throw;
        }
        finally { recovery.Discovery?.Finish(); }
    }

    private async ValueTask<RespireClusterScanPage> ScanClusterPageCoreAsync(
        RespireClusterScanCursor cursor, string? match, RespireKeyType? type,
        int countHint, CancellationToken cancellationToken, ScanRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);
        var typeToken = FormatKeyType(type);
        ObjectDisposedException.ThrowIf(client.Core.Disposed, client);
        cancellationToken.ThrowIfCancellationRequested();
        if (client.Core.Cluster is null) throw new InvalidOperationException("A Cluster scan requires UseCluster.");
        var prefix = client.KeyPrefix;
        var effectiveMatch = ScanMatch(prefix, match);
        // Keep the original text checkpoint representation. Binary prefixes additionally carry
        // their exact bytes, and Match records the caller's text pattern in that format.
        var checkpointMatch = prefix?.Text is { } text ? EscapeGlob(text) + (match ?? "*") : match;
        var binaryPrefix = prefix is { Text: null } ? prefix.Bytes : null;
        if (cursor.State is { } previous && (previous.Match != checkpointMatch || previous.Type != typeToken
            || !previous.MatchesKeyPrefix(prefix) || previous.Database is { } database && database != client.Core.Options.Database))
            throw new ArgumentException("Resume a Cluster scan with the same match, type, database and key prefix.", nameof(cursor));
        if (cursor.IsComplete) return new(cursor, []);
        // Never mutate a published cursor. Failure/cancellation leaves the caller's checkpoint intact.
        var state = cursor.State?.Copy() ?? new ClusterScanState(checkpointMatch, typeToken, prefix?.Text) { BinaryPrefix = binaryPrefix };
        // Legacy tokens cannot prove their original database. Adopt the caller's database
        // once and publish RSC3 so subsequent resumes enforce that identity.
        if (state.Database is null) state.Database = client.Core.Options.Database;
        var topology = await ReadScanTopologyAsync(cancellationToken, recovery.Discovery, recovery.Observation).ConfigureAwait(false);
        ReconcileScan(state, topology);
        RestrictScanToMatchingSlot(state, client.EncodedKeyPrefix is { HasSurrogateBoundary: true } ? null : effectiveMatch);
        var node = SelectScanNode(state, topology);
        if (node is null)
        {
            state.ResetPass();
            return new(new RespireClusterScanCursor(state), []) { WaitingOnMigration = true };
        }
        var runId = await ReadScanRunIdAsync(node.Connection, cancellationToken, recovery.Observation).ConfigureAwait(false);
        if (state.ActiveNode is not null && (state.ActiveNode != node.Metadata.Id
            || state.RunId != runId || state.Epoch != node.Metadata.ConfigurationEpoch)) state.ResetPass();
        // Finish an old numeric pass unchanged. Only new passes and opaque continuations
        // select the newer protocol; opaque positions are never sent to SCAN.
        if ((state.ActiveNode is null || state.ValkeyCursor is not null)
            && await SupportsClusterScanAsync(node.Connection, runId, recovery, cancellationToken).ConfigureAwait(false))
        {
            var modern = await ReadValkeyScanPageAsync(state, topology, node, runId, match, type,
                countHint, cancellationToken, recovery).ConfigureAwait(false);
            if (modern is not null) return modern;
        }
        if (state.ValkeyCursor is not null) state.ResetPass();
        if (state.ActiveNode != node.Metadata.Id || state.RunId != runId || state.Epoch != node.Metadata.ConfigurationEpoch)
        {
            state.ResetPass();
            state.ActiveNode = node.Metadata.Id;
            state.RunId = runId;
            state.Epoch = node.Metadata.ConfigurationEpoch;
            for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
                state.PassSlots[slot] = !state.Completed[slot] && state.Owners[slot] == state.ActiveNode && !topology.Moving[slot];
        }
        var serverCursor = state.Cursor.ToString(CultureInfo.InvariantCulture);
        var arguments = (effectiveMatch, typeToken) switch
        {
            (null, null) => new RespireValue[] { serverCursor, "COUNT", countHint },
            (not null, null) => [serverCursor, "MATCH", effectiveMatch.Value, "COUNT", countHint],
            (null, not null) => [serverCursor, "COUNT", countHint, "TYPE", typeToken],
            _ => [serverCursor, "MATCH", effectiveMatch!.Value, "COUNT", countHint, "TYPE", typeToken],
        };
        // A node-local cursor must never be redirected or transferred to a replacement server.
        using var reply = await client.SendOnPinnedConnectionAsync("SCAN", node.Connection,
            new CmdN(Verbs.Scan, arguments), cancellationToken, observation: recovery.Observation).ConfigureAwait(false);
        if (reply.Type != RespDataType.Array || reply.AsArray().Length != 2)
            throw new RespireProtocolException("SCAN must return a cursor and a key array.");
        var values = reply.AsArray();
        if (!ulong.TryParse(ClusterInspectionParser.Text(in values[0]), NumberStyles.None,
                CultureInfo.InvariantCulture, out var next) || values[1].Type != RespDataType.Array)
            throw new RespireProtocolException("SCAN returned an invalid cursor or key array.");
        List<string> keys = [];
        foreach (ref readonly var value in values[1].AsArray())
        {
            // Hash original bytes before string decoding, including binary keys and hash tags.
            if (value.Type != RespDataType.BulkString || value.IsNull)
                throw new RespireProtocolException("SCAN keys must be bulk strings.");
            if (state.Completed[ClusterHash.GetSlot(value.AsSpan())]) continue;
            if (prefix is null) keys.Add(value.AsString());
            else if (ScanKey(in value, prefix) is { } key) keys.Add(key);
        }
        state.Cursor = next;
        if (next == 0)
        {
            // Validate the complete pass against fresh, primary-local ownership and migration
            // state. An in-progress migration is never certified as a completed slot scan.
            await CompleteScanPassAsync(state, effectiveMatch, cancellationToken, recovery.Discovery, recovery.Observation).ConfigureAwait(false);
        }
        return new(new RespireClusterScanCursor(state), keys.ToArray());
    }

    private async ValueTask CompleteScanPassAsync(ClusterScanState state, RespireValue? effectiveMatch, CancellationToken cancellationToken,
        ClusterRouter.DiscoveryRound? discovery, RespireTelemetry.ErrorObservation observation)
    {
        var after = await ReadScanTopologyAsync(cancellationToken, discovery, observation).ConfigureAwait(false);
        ReconcileScan(state, after);
        RestrictScanToMatchingSlot(state, effectiveMatch);
        if (state.ActiveNode is { } active && after.Nodes.TryGetValue(active, out var current)
            && state.Epoch == current.Metadata.ConfigurationEpoch
            && state.RunId == await ReadScanRunIdAsync(current.Connection, cancellationToken, observation).ConfigureAwait(false))
            for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
                if (state.PassSlots[slot]) state.Completed[slot] = true;
        state.ResetPass();
    }

    private sealed record ScanNode(RespireConnection Connection, RespireClusterNode Metadata);
    private sealed record ScanTopology(Dictionary<string, ScanNode> Nodes, string[] Owners, bool[] Moving);

    private async ValueTask<ScanTopology> ReadScanTopologyAsync(
        CancellationToken cancellationToken, ClusterRouter.DiscoveryRound? discovery, RespireTelemetry.ErrorObservation observation)
    {
        RespireConnection[] connections;
        try
        {
            connections = await client.Core.Cluster!.GetMasterConnectionsAsync(cancellationToken, discovery).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not RespireConnectionRetiredException)
        {
            if (discovery is not null) discovery.TerminalError = error;
            throw;
        }
        var nodes = new Dictionary<string, ScanNode>(StringComparer.Ordinal);
        var owners = new string[ClusterHash.SlotCount];
        var moving = new bool[ClusterHash.SlotCount];
        foreach (var connection in connections)
        {
            using var reply = await client.SendOnPinnedConnectionAsync("CLUSTER NODES", connection,
                new Cmd(RespireCommands.Cluster.CLUSTER_NODES.Verb), cancellationToken, observation: observation).ConfigureAwait(false);
            var rows = ClusterInspectionParser.Nodes(in reply);
            var self = rows.SingleOrDefault(static row => row.Flags.Contains("myself", StringComparer.Ordinal));
            if (self is null || !self.Flags.Contains("master", StringComparer.Ordinal)
                || self.Id.Length is < 1 or > 128 || !nodes.TryAdd(self.Id, new(connection, self)))
                throw new RespireConnectionException("Unable to validate current Cluster primaries for SCAN; retry the same cursor.");
            foreach (var range in self.Slots)
            {
                for (var slot = range.Start; slot <= range.End; slot++)
                {
                    if (owners[slot] is not null)
                        throw new RespireConnectionException("Cluster ownership changed during SCAN discovery; retry the same cursor.");
                    owners[slot] = self.Id;
                }
            }
            foreach (var transition in self.Transitions) moving[transition.Slot] = true;
        }
        if (owners.Any(static owner => owner is null))
            throw new RespireConnectionException("Cluster SCAN requires complete slot coverage; retry the same cursor.");
        return new(nodes, owners, moving);
    }

    private async ValueTask<string> ReadScanRunIdAsync(RespireConnection connection, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        using var reply = await client.SendOnPinnedConnectionAsync("INFO", connection,
            new Cmd1(Verbs.Info, "server"), cancellationToken, observation: observation).ConfigureAwait(false);
        foreach (var line in ClusterInspectionParser.Text(in reply).Split('\n'))
            if (line.StartsWith("run_id:", StringComparison.Ordinal) && line.AsSpan(7).Trim().Length > 0)
                return line[7..].Trim();
        throw new RespireProtocolException("INFO server did not provide run_id for a resumable Cluster scan.");
    }

    private static void ReconcileScan(ClusterScanState state, ScanTopology topology)
    {
        for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
        {
            if (state.Owners[slot] != topology.Owners[slot])
            {
                state.Completed[slot] = false;
                state.Owners[slot] = topology.Owners[slot];
                state.PassSlots[slot] = false;
            }
            if (topology.Moving[slot])
            {
                state.Completed[slot] = false;
                state.PassSlots[slot] = false;
            }
        }
        if (state.ActiveNode is { } active && (!topology.Nodes.TryGetValue(active, out var node)
            || node.Metadata.ConfigurationEpoch != state.Epoch
            || state.ValkeyCursor is { } opaque && !state.PassSlots[ClusterHash.GetSlot(opaque)]
            || !state.PassSlots.Any(static eligible => eligible))) state.ResetPass();
    }

    private static void RestrictScanToMatchingSlot(ClusterScanState state, RespireValue? effectiveMatch)
    {
        if (effectiveMatch is not { } pattern) return;
        var bytes = pattern.AsKey().ToBytes();
        if (!ClusterHash.TryGetFixedPatternSlot(bytes, out var matchingSlot))
        {
            // Without metacharacters this is an exact physical key, including empty tags.
            if (bytes.AsSpan().IndexOfAny("*?[\\"u8) >= 0) return;
            matchingSlot = ClusterHash.GetSlot(bytes);
        }
        for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
            if (slot != matchingSlot)
            {
                // MATCH itself proves these slots have no eligible keys, even while moving.
                state.Completed[slot] = true;
                state.PassSlots[slot] = false;
            }
        if (state.ValkeyCursor is { } position && !state.PassSlots[ClusterHash.GetSlot(position)]) state.ResetPass();
    }

    private static ScanNode? SelectScanNode(ClusterScanState state, ScanTopology topology)
    {
        // Once all remaining slots are moving, no pass can certify progress. Return a
        // waiting page without issuing INFO/SCAN until stable work becomes available.
        // ReconcileScan has already removed missing nodes and passes with no eligible slots.
        for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
            if (!state.Completed[slot] && !topology.Moving[slot])
                return topology.Nodes[state.ActiveNode ?? state.Owners[slot]];
        return null;
    }
}
