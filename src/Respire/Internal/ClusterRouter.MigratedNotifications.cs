using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private const int RecentSmigratedSequenceLimit = 256;

    private sealed record QueuedSmigratedNotification(
        RespireConnectionMultiplexer Sender, MaintenanceNotification Notification, long TopologyVersion);

    private sealed class SmigratedSequenceWindow
    {
        private readonly Queue<long> _order = new();
        private readonly HashSet<long> _seen = [];

        internal bool Contains(long sequence) => _seen.Contains(sequence);

        internal void Add(long sequence)
        {
            if (!_seen.Add(sequence)) return;
            _order.Enqueue(sequence);
            if (_order.Count > RecentSmigratedSequenceLimit) _seen.Remove(_order.Dequeue());
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
    private readonly Dictionary<RespireConnectionMultiplexer, Action<RespireConnectionMultiplexer, MaintenanceNotification>> _nodeMaintenanceHandlers = [];
    private readonly Dictionary<RespireConnectionMultiplexer, SmigratedSequenceWindow> _smigratedSequences = [];
    private Task _smigratedWorker = Task.CompletedTask;

    private void StartSmigratedWorker()
        => _smigratedWorker = Task.Run(ProcessSmigratedNotificationsAsync);

    private void QueueSmigratedNotification(
        RespireConnectionMultiplexer sender, MaintenanceNotification notification)
    {
        if (notification.Kind != "SMIGRATED" || Volatile.Read(ref _disposed) != 0
            || notification.Migrations is not { Length: > 0 }) return;
        if (!Monitor.TryEnter(_nodesGate))
        {
            _logger?.LogDebug("Dropped Cluster SMIGRATED notification from {Host}:{Port} during a topology update.", sender.Host, sender.Port);
            return;
        }
        long topologyVersion;
        try { topologyVersion = _topologyVersion; }
        finally { Monitor.Exit(_nodesGate); }
        _smigratedNotifications.Writer.TryWrite(new(sender, notification, topologyVersion));
    }

    private async Task ProcessSmigratedNotificationsAsync()
    {
        try
        {
            await foreach (var item in _smigratedNotifications.Reader.ReadAllAsync(_stopDiscovery.Token).ConfigureAwait(false))
            {
                try
                {
                    ApplySmigratedNotification(item);
                }
                catch (Exception error) when (!_stopDiscovery.IsCancellationRequested)
                {
                    _logger?.LogError(error, "Failed to apply Cluster SMIGRATED notification from {Host}:{Port}.",
                        item.Sender.Host, item.Sender.Port);
                }
            }
        }
        catch (OperationCanceledException) when (_stopDiscovery.IsCancellationRequested)
        {
        }
    }

    private void ApplySmigratedNotification(QueuedSmigratedNotification item)
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
            if (Volatile.Read(ref _disposed) != 0 || !_identities.IsActive(item.Sender)
                || !_nodeMaintenanceHandlers.ContainsKey(item.Sender)) return;
            if (!_smigratedSequences.TryGetValue(item.Sender, out var sequences))
                _smigratedSequences.Add(item.Sender, sequences = new());
            if (sequences.Contains(item.Notification.SequenceId)) return;

            foreach (var (migration, slots) in parsed)
            {
                if (!_identities.TryGetExisting(migration.Source, out var source)
                    || !_identities.IsActive(source) || source.IsRetired || !HasCurrentSourceSlot(source, slots, item.TopologyVersion))
                    continue;

                if (ClusterNodeIdentityIndex.EndpointsEqual(migration.Source, migration.Target)) continue;
                var target = _identities.GetOrCreate(migration.Target);
                if (ReferenceEquals(source, target) || target.IsRetired) continue;
                ObserveNode(target);

                for (var index = 0; index < slots.Length; index++)
                {
                    var slot = slots[index];
                    if (_slotVersions[slot] > item.TopologyVersion
                        || !ReferenceEquals(Volatile.Read(ref _slots[slot]), source)) continue;
                    PublishSlotLocked(slot, target, ++_topologyVersion);
                    AddSlot(target);
                    topologyChanged = true;
                    if (RemoveSlot(source)) (retiredNodes ??= []).Add(source);
                }
            }

            sequences.Add(item.Notification.SequenceId);
            if (retiredNodes is not null)
            {
                var retained = new HashSet<RespireConnectionMultiplexer>(_masters);
                retirements = DetachGenerationsLocked(_identities.DetachInactive(retained, _seeds));
            }
        }

        if (Volatile.Read(ref _disposed) != 0) return;
        if (retirements is not null)
            foreach (var retirement in retirements) _ = DrainGenerationAsync(retirement);
        if (retiredNodes is not null)
            foreach (var node in retiredNodes) NodeRetired?.Invoke(node);
        if (topologyChanged) TopologyChanged?.Invoke();
    }

    private bool HasCurrentSourceSlot(
        RespireConnectionMultiplexer source, int[] slots, long topologyVersion)
    {
        foreach (var slot in slots)
        {
            if (_slotVersions[slot] <= topologyVersion
                && ReferenceEquals(Volatile.Read(ref _slots[slot]), source)) return true;
        }
        return false;
    }

    private static bool TryParseSlots(string value, out int[] slots)
    {
        slots = [];
        if (string.IsNullOrEmpty(value)) return false;
        var parsed = new List<int>();
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
                || parsed.Count + end - start + 1 > ClusterHash.SlotCount)
                return false;
            for (var slot = start; slot <= end; slot++) parsed.Add(slot);
            if (comma < 0) break;
            remaining = remaining[(comma + 1)..];
            if (remaining.IsEmpty) return false;
        }

        slots = [.. parsed];
        return slots.Length > 0;
    }
}
