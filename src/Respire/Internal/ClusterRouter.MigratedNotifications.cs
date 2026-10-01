using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private const int RecentSmigratedSequenceLimit = 256;

    // SequenceScope is the physical connection that received the push. Sequence IDs restart
    // with each connection, so deduplication never spans a reconnect, and items queued by an
    // old connection cannot mark a new connection's IDs as already seen.
    internal sealed record QueuedSmigratedNotification(
        RespireConnectionMultiplexer Sender, object SequenceScope,
        MaintenanceNotification Notification, long SlotMutationVersion);

    private sealed class SmigratedSequenceWindow
    {
        private readonly Queue<long> _order = new();
        private readonly HashSet<long> _seen = [];

        // Returns false when the ID was already seen inside the bounded window.
        internal bool TryAdd(long sequence)
        {
            if (!_seen.Add(sequence)) return false;
            _order.Enqueue(sequence);
            if (_order.Count > RecentSmigratedSequenceLimit) _seen.Remove(_order.Dequeue());
            return true;
        }
    }

    private readonly Channel<QueuedSmigratedNotification> _smigratedNotifications =
        Channel.CreateBounded<QueuedSmigratedNotification>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly Dictionary<RespireConnectionMultiplexer, Action<RespireConnectionMultiplexer, object, MaintenanceNotification>> _nodeMaintenanceHandlers = [];
    // Accessed only by the worker under _nodesGate. A window disappears with its connection.
    private readonly ConditionalWeakTable<object, SmigratedSequenceWindow> _smigratedSequences = new();
    private Task _smigratedWorker = Task.CompletedTask;
    [ThreadStatic]
    private static ClusterRouter? _smigratedWorkerContext;

    private bool IsOnSmigratedWorker => ReferenceEquals(_smigratedWorkerContext, this);

    private void StartSmigratedWorker()
        => _smigratedWorker = Task.Run(ProcessSmigratedNotificationsAsync);

    // Receive-loop callback: never takes _nodesGate. The worker re-validates under the gate.
    private void QueueSmigratedNotification(
        RespireConnectionMultiplexer sender, object sequenceScope, MaintenanceNotification notification)
    {
        // Capture the mutation fence before validation; otherwise a pause between validation
        // and enqueue could give an old receive callback a token newer than intervening routes.
        var item = CaptureSmigratedNotification(sender, sequenceScope, notification);
        if (notification.Kind != "SMIGRATED" || Volatile.Read(ref _disposed) != 0
            || notification.Migrations is not { Length: > 0 }) return;
        // This callback is attached only while the sender is active. Preserve that enqueue-time
        // validity: an earlier FIFO item can retire the sender before a later queued item runs.
        _smigratedNotifications.Writer.TryWrite(item);
    }

    internal QueuedSmigratedNotification CaptureSmigratedNotification(
        RespireConnectionMultiplexer sender, object sequenceScope, MaintenanceNotification notification)
        => new(sender, sequenceScope, notification, Interlocked.Increment(ref _slotMutationVersion));

    // MOVED, slot clears, discovery owner changes and SMIGRATED callback entry advance this
    // fence. The worker writes each notification's entry token to its slots, preserving FIFO
    // chains while rejecting a callback that was overtaken before it could enter the channel.
    private void MarkSlotMutatedLocked(int slot)
        => _slotMutationVersions[slot] = Interlocked.Increment(ref _slotMutationVersion);

    // Ends when DisposeAsync completes the channel. It deliberately takes no cancellation
    // token: a registration on _stopDiscovery would make disposal's CancelAsync asynchronous.
    private async Task ProcessSmigratedNotificationsAsync()
    {
        await foreach (var item in _smigratedNotifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var previousWorker = _smigratedWorkerContext;
            _smigratedWorkerContext = this;
            try
            {
                ApplySmigratedNotification(item);
            }
            catch (Exception error)
            {
                if (Volatile.Read(ref _disposed) == 0)
                    _logger?.LogError(error, "Failed to apply Cluster SMIGRATED notification from {Host}:{Port}.",
                        item.Sender.Host, item.Sender.Port);
            }
            finally
            {
                _smigratedWorkerContext = previousWorker;
            }
        }
    }

    internal void ApplySmigratedNotification(QueuedSmigratedNotification item)
    {
        if (item.Notification.Migrations is not { Length: > 0 } migrations) return;
        var parsed = new List<(MaintenanceSlotMigration Migration, int[] Slots)>(migrations.Length);
        var totalSlots = 0;
        foreach (var migration in migrations)
        {
            if (!TryParseSlots(migration.Slots, out var slots)) return;
            totalSlots += slots.Length;
            if (totalSlots > ClusterHash.SlotCount) return;
            parsed.Add((migration, slots));
        }

        List<RespireConnectionMultiplexer>? retiredNodes = null;
        List<RetiredGeneration>? retirements = null;
        var topologyChanged = false;
        lock (_nodesGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!_smigratedSequences.GetOrCreateValue(item.SequenceScope).TryAdd(item.Notification.SequenceId)) return;

            List<int>? movable = null;
            foreach (var (migration, slots) in parsed)
            {
                if (ClusterNodeIdentityIndex.EndpointsEqual(migration.Source, migration.Target)
                    || !_identities.TryGetExisting(migration.Source, out var source)
                    || !_identities.IsActive(source) || source.IsRetired) continue;

                // Select slots before resolving the target, so a fully fenced migration
                // cannot leave an observed, slotless target transport behind.
                (movable ??= []).Clear();
                foreach (var slot in slots)
                {
                    if (_slotMutationVersions[slot] <= item.SlotMutationVersion
                        && ReferenceEquals(Volatile.Read(ref _slots[slot]), source)) movable.Add(slot);
                }
                if (movable.Count == 0) continue;

                var target = _identities.GetOrCreate(migration.Target);
                if (ReferenceEquals(source, target) || target.IsRetired) continue;
                ObserveNode(target);

                // One discovery fence per migration: older in-flight CLUSTER SLOTS replies
                // cannot overwrite these slots.
                var migrationVersion = ++_topologyVersion;
                foreach (var slot in movable)
                {
                    PublishSlotLocked(slot, target, migrationVersion);
                    _slotMutationVersions[slot] = item.SlotMutationVersion;
                    AddSlot(target);
                    if (RemoveSlot(source)) (retiredNodes ??= []).Add(source);
                }
                topologyChanged = true;
            }

            if (retiredNodes is not null)
            {
                // Mirror ApplyTopology: keep ASK-protected transports and repoint the seed.
                var retained = new HashSet<RespireConnectionMultiplexer>(_masters);
                retained.UnionWith(_redirectVersions.Keys);
                if (Volatile.Read(ref _seed) is { } previousSeed) SetSeedLocked(previousSeed);
                retirements = DetachGenerationsLocked(_identities.DetachInactive(retained, _seeds));
                if (Volatile.Read(ref _seed) is { } seed) SetSeedLocked(seed);
            }
        }

        // Launch retirements before the disposal check: DisposeAsync awaits their completion.
        if (retirements is not null)
            foreach (var retirement in retirements) _ = DrainGenerationAsync(retirement);
        if (Volatile.Read(ref _disposed) != 0) return;
        if (retiredNodes is not null)
            foreach (var node in retiredNodes) NodeRetired?.Invoke(node);
        if (topologyChanged) TopologyChanged?.Invoke();
    }

    private static bool TryParseSlots(string value, out int[] slots)
    {
        slots = [];
        if (string.IsNullOrEmpty(value)) return false;
        var parsed = new List<int>();
        var seenSlots = new HashSet<int>();
        // Bound enumeration, not just retained slots: repeated full ranges in one bounded string
        // could otherwise cost hundreds of millions of lookups on the single worker.
        var enumerated = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf(',');
            var range = comma < 0 ? remaining : remaining[..comma];
            var dash = range.IndexOf('-');
            var first = dash < 0 ? range : range[..dash];
            var last = dash < 0 ? first : range[(dash + 1)..];
            if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                || !int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                || start is < 0 or >= ClusterHash.SlotCount
                || end < start || end >= ClusterHash.SlotCount
                || (enumerated += end - start + 1) > ClusterHash.SlotCount)
                return false;
            for (var slot = start; slot <= end; slot++)
                if (seenSlots.Add(slot)) parsed.Add(slot);
            if (comma < 0) break;
            remaining = remaining[(comma + 1)..];
            if (remaining.IsEmpty) return false;
        }

        slots = [.. parsed];
        return slots.Length > 0;
    }
}
