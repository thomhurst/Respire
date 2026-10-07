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
            || IsUnknownScanCommand(error, "COMMAND"))
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
        if (!BeginValkeyOwnerRange(state, topology, node, runId, out var slot))
            return new(new RespireClusterScanCursor(state), []) { WaitingOnMigration = true };
        var command = CreateValkeyClusterScanCommand(client, state.ValkeyCursor ?? "0", match, type,
            countHint, starting ? slot : null, cancellationToken);
        var sent = await SendValkeyScanWithRedirectsAsync(state, topology, node, runId, command,
            slot, starting, cancellationToken, recovery).ConfigureAwait(false);
        if (sent.Unsupported)
            return sent.Redirected && !starting ? new(new RespireClusterScanCursor(state), []) : null;
        return await ApplyValkeyScanPageAsync(state, sent, slot, starting, match,
            cancellationToken, recovery.Discovery).ConfigureAwait(false);
    }

    private static bool BeginValkeyOwnerRange(ClusterScanState state, ScanTopology topology,
        ScanNode node, string runId, out int slot)
    {
        if (state.ValkeyCursor is { } position)
        {
            slot = ClusterHash.GetSlot(position);
            return true;
        }
        slot = 0;
        while (slot < ClusterHash.SlotCount && (state.Completed[slot] || topology.Moving[slot]
            || state.Owners[slot] != node.Metadata.Id)) slot++;
        state.ResetPass();
        if (slot == ClusterHash.SlotCount) return false;
        state.ActiveNode = node.Metadata.Id;
        state.RunId = runId;
        state.Epoch = node.Metadata.ConfigurationEpoch;
        // Only the contiguous, validated owner range can be certified by this pass.
        for (var current = slot; current < ClusterHash.SlotCount && state.Owners[current] == state.ActiveNode; current++)
            state.PassSlots[current] = !state.Completed[current] && !topology.Moving[current];
        return true;
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
                    cancellationToken, sendAsking: asking).ConfigureAwait(false);
                return new(reply, redirected, Unsupported: false);
            }
            catch (RespireServerException error) when (IsUnknownScanCommand(error, "CLUSTERSCAN"))
            {
                // This is evidence about the actual destination, never about the redirect source.
                Volatile.Write(ref _scanCapabilities!.GetOrCreateValue(connection).Evidence, new(runId, false));
                state.ResetPass();
                return new(default, redirected, Unsupported: true);
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
                    return new(default, redirected, Unsupported: true);
                }
            }
        }
    }

    private async ValueTask<RespireClusterScanPage> ApplyValkeyScanPageAsync(ClusterScanState state,
        ValkeyScanReply sent, int slot, bool starting, string? match,
        CancellationToken cancellationToken, ClusterRouter.DiscoveryRound? discovery)
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
            // Literal "0" may itself be redirected before the server creates the SLOT
            // cursor. That empty bootstrap has scanned no data and certifies no slots;
            // keep its opaque position for the original, validated slot owner.
            var bootstrap = starting && raw.Length == 0 && nextCursor != "0" && ClusterHash.GetSlot(nextCursor) == slot;
            if (sent.Redirected && !bootstrap) state.ResetPass(); // A scanned redirect cannot certify the old owner's range.
            else if (nextCursor == "0")
            {
                // The bootstrap uses SLOT, so a terminal reply only certifies that slot.
                if (starting)
                    for (var current = 0; current < ClusterHash.SlotCount; current++) state.PassSlots[current] &= current == slot;
                await CompleteScanPassAsync(state, effectiveMatch, cancellationToken, discovery).ConfigureAwait(false);
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
                    await CompleteScanPassAsync(state, effectiveMatch, cancellationToken, discovery).ConfigureAwait(false);
                }
                else state.ValkeyCursor = nextCursor;
            }
            return new(new RespireClusterScanCursor(state), keys.ToArray());
        }
    }

    private static bool IsUnknownScanCommand(RespireServerException error, string command)
    {
        const string prefix = "ERR unknown command ";
        if (error.Code != "ERR" || !error.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var name = error.Message.AsSpan(prefix.Length).TrimStart();
        if (name.IsEmpty) return false;
        if (name[0] is '\'' or '"')
        {
            var end = name[1..].IndexOf(name[0]);
            return end >= 0 && name.Slice(1, end).Equals(command, StringComparison.OrdinalIgnoreCase);
        }
        var separator = name.IndexOfAny(' ', ',');
        return (separator < 0 ? name : name[..separator]).Equals(command, StringComparison.OrdinalIgnoreCase);
    }
}
