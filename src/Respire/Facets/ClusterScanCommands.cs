using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

internal sealed partial class KeyCommands
{
    private ConditionalWeakTable<RespireConnection, ScanCapability>? _scanCapabilities;
    private sealed record ScanCapabilityEvidence(string RunId, bool Supported);
    private sealed class ScanCapability { internal ScanCapabilityEvidence? Evidence; }
    private sealed class ScanRecovery
    {
        internal int Rejections;
        internal ClusterRouter.DiscoveryRound? Discovery;
    }

    public async ValueTask<RespireClusterScanPage> ScanClusterPageAsync(
        RespireClusterScanCursor cursor, string? match = null, RespireKeyType? type = null,
        int countHint = 250, CancellationToken cancellationToken = default)
    {
        var recovery = new ScanRecovery();
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
        state.Database ??= client.Core.Options.Database;
        var topology = await ReadScanTopologyAsync(cancellationToken, recovery.Discovery).ConfigureAwait(false);
        ReconcileScan(state, topology);
        var node = SelectScanNode(state, topology);
        if (node is null)
        {
            state.ResetPass();
            return new(new RespireClusterScanCursor(state), []) { WaitingOnMigration = true };
        }
        var runId = await ReadScanRunIdAsync(node.Connection, cancellationToken).ConfigureAwait(false);
        if (state.ActiveNode is not null && (state.ActiveNode != node.Metadata.Id
            || state.RunId != runId || state.Epoch != node.Metadata.ConfigurationEpoch)) state.ResetPass();
        // Finish an old numeric pass unchanged. Only new passes and opaque continuations
        // select the newer protocol; opaque positions are never sent to SCAN.
        if ((state.ActiveNode is null || state.ValkeyCursor is not null)
            && await SupportsClusterScanAsync(node.Connection, runId, cancellationToken).ConfigureAwait(false))
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
            if (prefix is null) keys.Add(value.AsString());
            else if (ScanKey(in value, prefix) is { } key) keys.Add(key);
        }
        state.Cursor = next;
        if (next == 0)
        {
            // Validate the complete pass against fresh, primary-local ownership and migration
            // state. An in-progress migration is never certified as a completed slot scan.
            await CompleteScanPassAsync(state, cancellationToken, recovery.Discovery).ConfigureAwait(false);
        }
        return new(new RespireClusterScanCursor(state), keys.ToArray());
    }

    private async ValueTask<bool> SupportsClusterScanAsync(RespireConnection connection, string runId,
        CancellationToken cancellationToken)
    {
        var cache = _scanCapabilities;
        if (cache is null)
        {
            var created = new ConditionalWeakTable<RespireConnection, ScanCapability>();
            cache = Interlocked.CompareExchange(ref _scanCapabilities, created, null) ?? created;
        }
        var capability = cache.GetOrCreateValue(connection);
        if (Volatile.Read(ref capability.Evidence) is { } known && known.RunId == runId) return known.Supported;
        try
        {
            using var reply = await client.SendOnPinnedConnectionAsync("COMMAND INFO", connection,
                new Cmd1(RespireCommands.Server.COMMAND_INFO.Verb, "CLUSTERSCAN"), cancellationToken).ConfigureAwait(false);
            if (reply.Type != RespDataType.Array || reply.AsArray().Length != 1) return false;
            var entry = reply.AsArray()[0];
            bool supported;
            if (entry.IsNull) supported = false;
            else if (entry.Type == RespDataType.Array && entry.AsArray().Length > 0
                && entry.AsArray()[0].Type is RespDataType.BulkString or RespDataType.SimpleString
                && ClusterInspectionParser.Text(in entry.AsArray()[0]).Equals("clusterscan", StringComparison.OrdinalIgnoreCase))
                supported = true;
            else return false;
            Volatile.Write(ref capability.Evidence, new(runId, supported));
            return supported;
        }
        catch (RespireServerException error) when (error.Code == "NOPERM"
            || error.Message.StartsWith("ERR unknown command ", StringComparison.OrdinalIgnoreCase))
        {
            // Unknown metadata permits a legacy pass, but is not cached as command absence.
            return false;
        }
    }

    private async ValueTask<RespireClusterScanPage?> ReadValkeyScanPageAsync(ClusterScanState state,
        ScanTopology topology, ScanNode node, string runId, string? match, RespireKeyType? type,
        int countHint, CancellationToken cancellationToken, ScanRecovery recovery)
    {
        var starting = state.ValkeyCursor is null;
        var slot = starting ? 0 : ClusterHash.GetSlot(state.ValkeyCursor!);
        if (starting)
        {
            while (state.Completed[slot] || topology.Moving[slot] || state.Owners[slot] != node.Metadata.Id) slot++;
            state.ActiveNode = node.Metadata.Id;
            state.RunId = runId;
            state.Epoch = node.Metadata.ConfigurationEpoch;
            // CLUSTERSCAN scans the contiguous owner range, then returns a cursor for
            // another range. Only this validated range can be certified by this pass.
            for (var current = slot; current < ClusterHash.SlotCount && state.Owners[current] == state.ActiveNode; current++)
                state.PassSlots[current] = !state.Completed[current] && !topology.Moving[current];
        }
        var command = CreateValkeyClusterScanCommand(client, state.ValkeyCursor ?? "0", match, type,
            countHint, starting ? slot : null, cancellationToken);
        var connection = node.Connection;
        var redirected = false;
        var asking = false;
        RespValue reply;
        while (true)
        {
            try
            {
                reply = asking
                    ? await client.SendOnConnectionAsync("CLUSTERSCAN", connection, command, cancellationToken,
                        sendAsking: true, allowStreamingConnectionReroute: false).ConfigureAwait(false)
                    : await client.SendOnPinnedConnectionAsync("CLUSTERSCAN", connection, command, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (RespireServerException error) when (error.Code == "ERR"
                && error.Message.StartsWith("ERR unknown command 'CLUSTERSCAN'", StringComparison.OrdinalIgnoreCase))
            {
                // This is evidence about the actual destination, never about the redirect source.
                Volatile.Write(ref _scanCapabilities!.GetOrCreateValue(connection).Evidence, new(runId, false));
                state.ResetPass();
                return redirected && !starting ? new(new RespireClusterScanCursor(state), []) : null;
            }
            catch (RespireServerException error) when (client.Core.Cluster is { } cluster
                && cluster.CanRetryRetirement(recovery.Rejections, cancellationToken) && ClusterRouter.IsRedirect(error))
            {
                redirected = true;
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
                runId = await ReadScanRunIdAsync(connection, cancellationToken).ConfigureAwait(false);
                if (!await SupportsClusterScanAsync(connection, runId, cancellationToken).ConfigureAwait(false))
                {
                    state.ResetPass();
                    return starting ? null : new(new RespireClusterScanCursor(state), []);
                }
            }
        }
        using (reply)
        {
            // Keep the page API's existing string decoding/prefix contract. The typed
            // primitive remains binary-safe for callers needing byte-preserving keys.
            var nextCursor = ValkeyClusterScanParser.ReadCursor(in reply);
            var raw = reply.AsArray()[1].AsArray();
            var keys = new List<string>(raw.Length);
            var prefix = client.EncodedKeyPrefix;
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
            // Literal "0" may itself be redirected before the server creates the SLOT
            // cursor. That empty bootstrap has scanned no data and certifies no slots;
            // keep its opaque position for the original, validated slot owner.
            var bootstrap = starting && raw.Length == 0 && nextCursor != "0" && ClusterHash.GetSlot(nextCursor) == slot;
            if (redirected && !bootstrap) state.ResetPass(); // A scanned redirect cannot certify the old owner's range.
            else if (nextCursor == "0")
            {
                // The bootstrap uses SLOT, so a terminal reply only certifies that slot.
                if (starting)
                    for (var current = 0; current < ClusterHash.SlotCount; current++) state.PassSlots[current] &= current == slot;
                await CompleteScanPassAsync(state, cancellationToken, recovery.Discovery).ConfigureAwait(false);
            }
            else
            {
                var nextSlot = ClusterHash.GetSlot(nextCursor);
                if (nextSlot < slot) throw new RespireProtocolException("CLUSTERSCAN moved its cursor backwards.");
                if (!state.PassSlots[nextSlot])
                {
                    // The next opaque position belongs to another range (or an already
                    // completed/migrating slot). Certify only slots strictly before it.
                    for (var current = nextSlot; current < ClusterHash.SlotCount; current++) state.PassSlots[current] = false;
                    await CompleteScanPassAsync(state, cancellationToken, recovery.Discovery).ConfigureAwait(false);
                }
                else state.ValkeyCursor = nextCursor;
            }
            return new(new RespireClusterScanCursor(state), keys.ToArray());
        }
    }

    private async ValueTask CompleteScanPassAsync(ClusterScanState state, CancellationToken cancellationToken,
        ClusterRouter.DiscoveryRound? discovery)
    {
        var after = await ReadScanTopologyAsync(cancellationToken, discovery).ConfigureAwait(false);
        ReconcileScan(state, after);
        if (state.ActiveNode is { } active && after.Nodes.TryGetValue(active, out var current)
            && state.Epoch == current.Metadata.ConfigurationEpoch
            && state.RunId == await ReadScanRunIdAsync(current.Connection, cancellationToken).ConfigureAwait(false))
            for (var slot = 0; slot < ClusterHash.SlotCount; slot++)
                if (state.PassSlots[slot]) state.Completed[slot] = true;
        state.ResetPass();
    }

    private sealed record ScanNode(RespireConnection Connection, RespireClusterNode Metadata);
    private sealed record ScanTopology(Dictionary<string, ScanNode> Nodes, string[] Owners, bool[] Moving);

    private async ValueTask<ScanTopology> ReadScanTopologyAsync(
        CancellationToken cancellationToken, ClusterRouter.DiscoveryRound? discovery)
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
        using var reply = await client.SendOnPinnedConnectionAsync("INFO", connection,
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
            || state.ValkeyCursor is { } opaque && !state.PassSlots[ClusterHash.GetSlot(opaque)]
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
