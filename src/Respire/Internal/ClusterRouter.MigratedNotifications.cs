using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private readonly Channel<(RespireConnectionMultiplexer Sender, MaintenanceNotification Notification)> _smigratedNotifications =
        Channel.CreateBounded<(RespireConnectionMultiplexer, MaintenanceNotification)>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly Dictionary<RespireConnectionMultiplexer, Action<RespireConnectionMultiplexer, MaintenanceNotification>> _nodeMaintenanceHandlers = [];
    private readonly Dictionary<RespireConnectionMultiplexer, long> _lastSmigratedSequences = [];
    private Task _smigratedWorker = Task.CompletedTask;

    private void StartSmigratedWorker()
        => _smigratedWorker = Task.Run(ProcessSmigratedNotificationsAsync);

    private void QueueSmigratedNotification(
        RespireConnectionMultiplexer sender, MaintenanceNotification notification)
    {
        if (notification.Kind == "SMIGRATED" && Volatile.Read(ref _disposed) == 0)
            _smigratedNotifications.Writer.TryWrite((sender, notification));
    }

    private async Task ProcessSmigratedNotificationsAsync()
    {
        try
        {
            await foreach (var item in _smigratedNotifications.Reader.ReadAllAsync(_stopDiscovery.Token).ConfigureAwait(false))
            {
                try
                {
                    ApplySmigratedNotification(item.Sender, item.Notification);
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

    private void ApplySmigratedNotification(
        RespireConnectionMultiplexer sender, MaintenanceNotification notification)
    {
        if (notification.Migrations is not { Length: > 0 } migrations) return;
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
        var topologyChanged = false;
        lock (_nodesGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_identities.IsActive(sender)
                || !_nodeMaintenanceHandlers.ContainsKey(sender)) return;
            if (_lastSmigratedSequences.TryGetValue(sender, out var lastSequence)
                && notification.SequenceId <= lastSequence) return;
            _lastSmigratedSequences[sender] = notification.SequenceId;

            foreach (var (migration, slots) in parsed)
            {
                if (!_identities.TryGetExisting(migration.Source, out var source)
                    || !_identities.IsActive(source) || source.IsRetired || slots.All(slot =>
                        !ReferenceEquals(Volatile.Read(ref _slots[slot]), source)))
                {
                    continue;
                }

                if (ClusterNodeIdentityIndex.EndpointsEqual(migration.Source, migration.Target)) continue;
                var target = _identities.GetOrCreate(migration.Target);
                if (ReferenceEquals(source, target) || target.IsRetired) continue;
                ObserveNode(target);

                foreach (var slot in slots)
                {
                    if (!ReferenceEquals(Volatile.Read(ref _slots[slot]), source)) continue;
                    PublishSlotLocked(slot, target, ++_topologyVersion);
                    AddSlot(target);
                    topologyChanged = true;
                    if (RemoveSlot(source)) (retiredNodes ??= []).Add(source);
                }
            }
        }

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
