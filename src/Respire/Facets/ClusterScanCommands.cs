using System.Globalization;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

internal sealed partial class KeyCommands
{
    public async ValueTask<RespireClusterScanPage> ScanClusterPageAsync(
        RespireClusterScanCursor cursor, string? match = null, RespireKeyType? type = null,
        int countHint = 250, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ScanClusterPageCoreAsync(cursor, match, type, countHint, cancellationToken).ConfigureAwait(false);
            }
            catch (RespireConnectionRetiredException) when (client.Core.Cluster is { } cluster
                && cluster.CanRetryRetirement(attempt, cancellationToken))
            {
                // Rejected admission cannot transfer a node-local cursor. Rebuild the page
                // from its immutable input and rediscover the current identities instead.
            }
        }
    }

    private async ValueTask<RespireClusterScanPage> ScanClusterPageCoreAsync(
        RespireClusterScanCursor cursor, string? match, RespireKeyType? type,
        int countHint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);
        var typeToken = FormatKeyType(type);
        ObjectDisposedException.ThrowIf(client.Core.Disposed, client);
        cancellationToken.ThrowIfCancellationRequested();
        if (client.Core.Cluster is null) throw new InvalidOperationException("A Cluster scan requires UseCluster.");
        var prefix = client.KeyPrefix;
        var effectiveMatch = prefix is null ? match : EscapeGlob(prefix) + (match ?? "*");
        if (cursor.State is { } previous && (previous.Match != effectiveMatch || previous.Type != typeToken || previous.Prefix != prefix))
            throw new ArgumentException("Resume a Cluster scan with the same match, type and key prefix.", nameof(cursor));
        if (cursor.IsComplete) return new(cursor, []);
        // Never mutate a published cursor. Failure/cancellation leaves the caller's checkpoint intact.
        var state = cursor.State?.Copy() ?? new ClusterScanState(effectiveMatch, typeToken, prefix);
        var topology = await ReadScanTopologyAsync(cancellationToken).ConfigureAwait(false);
        ReconcileScan(state, topology);
        var node = SelectScanNode(state, topology);
        if (node is null)
        {
            state.ResetPass();
            return new(new RespireClusterScanCursor(state), []) { WaitingOnMigration = true };
        }
        var runId = await ReadScanRunIdAsync(node.Connection, cancellationToken).ConfigureAwait(false);
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
            (not null, null) => [serverCursor, "MATCH", effectiveMatch, "COUNT", countHint],
            (null, not null) => [serverCursor, "COUNT", countHint, "TYPE", typeToken],
            _ => [serverCursor, "MATCH", effectiveMatch, "COUNT", countHint, "TYPE", typeToken],
        };
        // A node-local cursor must never be redirected or transferred to a replacement server.
        using var reply = await client.SendOnConnectionAsync("SCAN", node.Connection,
            new CmdN(Verbs.Scan, arguments), cancellationToken).ConfigureAwait(false);
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
            var key = value.AsString();
            if (prefix is null) keys.Add(key);
            else if (key.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(key[prefix.Length..]);
        }
        state.Cursor = next;
        if (next == 0)
        {
            // Validate the complete pass against fresh, primary-local ownership and migration
            // state. An in-progress migration is never certified as a completed slot scan.
            var after = await ReadScanTopologyAsync(cancellationToken).ConfigureAwait(false);
            ReconcileScan(state, after);
            if (state.ActiveNode is { } active && after.Nodes.TryGetValue(active, out var current)
                && state.Epoch == current.Metadata.ConfigurationEpoch
                && state.RunId == await ReadScanRunIdAsync(current.Connection, cancellationToken).ConfigureAwait(false))
            {
                for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
                    if (state.PassSlots[slot]) state.Completed[slot] = true;
            }
            state.ResetPass();
        }
        return new(new RespireClusterScanCursor(state), keys.ToArray());
    }

    private sealed record ScanNode(RespireConnection Connection, RespireClusterNode Metadata);
    private sealed record ScanTopology(Dictionary<string, ScanNode> Nodes, string[] Owners, bool[] Moving);

    private async ValueTask<ScanTopology> ReadScanTopologyAsync(CancellationToken cancellationToken)
    {
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(cancellationToken, discovery: null).ConfigureAwait(false);
        var nodes = new Dictionary<string, ScanNode>(StringComparer.Ordinal);
        var owners = new string[ClusterHash.SlotCount];
        var moving = new bool[ClusterHash.SlotCount];
        foreach (var connection in connections)
        {
            using var reply = await client.SendOnConnectionAsync("CLUSTER NODES", connection,
                new Cmd(RespireCommands.Cluster.CLUSTER_NODES.Verb), cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<string> ReadScanRunIdAsync(RespireConnection connection, CancellationToken cancellationToken)
    {
        using var reply = await client.SendOnConnectionAsync("INFO", connection,
            new Cmd1(Verbs.Info, "server"), cancellationToken).ConfigureAwait(false);
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
            || !state.PassSlots.Any(static eligible => eligible))) state.ResetPass();
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
