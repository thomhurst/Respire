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
    private const int SmigratedQueueCapacity = 128;
    private const int DeferredSmigratedMigrationLimit = 64;
    private const long SmigratedDropWarningIntervalMilliseconds = 30_000;

    // SequenceScope is the physical connection that received the push. Sequence IDs restart
    // with each connection, so deduplication never spans a reconnect, and items queued by an
    // old connection cannot mark a new connection's IDs as already seen.
    // SlotMutationVersion is the ClusterSlotMutationClock value read when the push was parsed.
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

    // A migration entry whose slots were not yet owned by its advertised source when it ran.
    // Receive loops on different connections can enqueue dependent notifications out of order
    // (B->C before A->B). The entry is retried after later migrations apply, under its original
    // fence token, so a MOVED, discovery change or newer migration still rejects it.
    private sealed class DeferredSmigratedMigration(
        RespireEndpoint source, RespireEndpoint target, int[] slots, long token)
    {
        internal readonly RespireEndpoint Source = source;
        internal readonly RespireEndpoint Target = target;
        internal readonly long Token = token;
        internal int[] Slots = slots;
    }

    private readonly Channel<QueuedSmigratedNotification> _smigratedNotifications;
    private readonly Dictionary<RespireConnectionMultiplexer, MaintenanceNotificationHandler> _nodeMaintenanceHandlers = [];
    // Accessed only by the worker under _nodesGate. A window disappears with its connection.
    private readonly ConditionalWeakTable<object, SmigratedSequenceWindow> _smigratedSequences = new();
    // Accessed only under _nodesGate. Oldest first; bounded by entry count and total slots.
    private readonly List<DeferredSmigratedMigration> _deferredSmigratedMigrations = [];
    private int _deferredSmigratedSlots;
    // Started on the first queued notification, so routers that never see SMIGRATED own no task.
    // DisposeAsync swaps in a completed task, after which no worker can start.
    private Task? _smigratedWorker;
    private long _smigratedNotificationsDropped;
    private long _lastSmigratedDropWarning = long.MinValue;
    // Set only while the worker runs ApplySmigratedNotification and its callbacks. Being
    // thread-static, it does not flow into tasks a callback starts, so their disposal still
    // joins the worker. ApplySmigratedNotification must therefore stay synchronous: an await
    // inside it would resume without the marker. ClientCore captures it before its own awaits.
    [ThreadStatic]
    private static ClusterRouter? _smigratedWorkerContext;

    internal bool IsOnSmigratedWorker => ReferenceEquals(_smigratedWorkerContext, this);

    internal long SmigratedNotificationsDropped => Interlocked.Read(ref _smigratedNotificationsDropped);

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private Channel<QueuedSmigratedNotification> CreateSmigratedChannel()
        => Channel.CreateBounded<QueuedSmigratedNotification>(new BoundedChannelOptions(SmigratedQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        }, OnSmigratedNotificationDropped);

    // Runs on the receive loop that overflowed the queue: count it and warn at a bounded rate.
    private void OnSmigratedNotificationDropped(QueuedSmigratedNotification dropped)
    {
        Interlocked.Increment(ref _smigratedNotificationsDropped);
        RecordSmigratedSkipped("queue_full", dropped.Sender);
        if (_logger is null) return;
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastSmigratedDropWarning);
        if (now - last < SmigratedDropWarningIntervalMilliseconds
            || Interlocked.CompareExchange(ref _lastSmigratedDropWarning, now, last) != last) return;
        _logger.LogWarning(
            "Cluster SMIGRATED queue is full; dropped the oldest notification (from {Host}:{Port}). {Dropped} dropped so far. MOVED handling and topology discovery will correct the affected slots.",
            dropped.Sender.Host, dropped.Sender.Port, SmigratedNotificationsDropped);
    }

    private static void RecordSmigratedSkipped(string reason, RespireConnectionMultiplexer sender, long count = 1)
    {
        if (!RespireTelemetry.ClusterSlotMigrationsSkipped.Enabled) return;
        RespireTelemetry.ClusterSlotMigrationsSkipped.Add(count,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("server.address", sender.Host),
            new KeyValuePair<string, object?>("server.port", sender.Port));
    }

    private void EnsureSmigratedWorker()
    {
        if (Volatile.Read(ref _smigratedWorker) is not null) return;
        var start = new Task<Task>(ProcessSmigratedNotificationsAsync);
        if (Interlocked.CompareExchange(ref _smigratedWorker, start.Unwrap(), null) is null)
            start.Start(TaskScheduler.Default);
    }

    // Disposal only: returns the worker to join, or a completed task if none ever started.
    private Task CloseSmigratedWorker()
        => Interlocked.CompareExchange(ref _smigratedWorker, Task.CompletedTask, null) ?? Task.CompletedTask;

    // Receive-loop callback: never takes _nodesGate. The worker re-validates under the gate.
    // The fence token was read by the receive loop when it parsed the push, before any other
    // maintenance processing, so an owner change made since then is newer than this item.
    private void QueueSmigratedNotification(RespireConnectionMultiplexer sender, object sequenceScope,
        MaintenanceNotification notification, long slotMutationToken)
    {
        if (!notification.IsSlotMigration || Volatile.Read(ref _disposed) != 0
            || notification.Migrations is not { Length: > 0 }) return;
        EnsureSmigratedWorker();
        // This callback is attached only while the sender is active. Preserve that enqueue-time
        // validity: an earlier FIFO item can retire the sender before a later queued item runs.
        _smigratedNotifications.Writer.TryWrite(new(sender, sequenceScope, notification, slotMutationToken));
    }

    // Test seam: stamps a notification as received now.
    internal QueuedSmigratedNotification CaptureSmigratedNotification(
        RespireConnectionMultiplexer sender, object sequenceScope, MaintenanceNotification notification)
        => new(sender, sequenceScope, notification, ClusterSlotMutationClock.Next());

    private void MarkSlotMutatedLocked(int slot)
        => _slotMutationVersions[slot] = ClusterSlotMutationClock.Next();

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

    // Must stay synchronous; see _smigratedWorkerContext.
    internal void ApplySmigratedNotification(QueuedSmigratedNotification item)
    {
        if (item.Notification.Migrations is not { Length: > 0 } migrations) return;
        var parsed = ParseMigrations(item, migrations);
        if (parsed.Count == 0) return;

        List<RespireConnectionMultiplexer>? retiredNodes = null;
        List<RetiredGeneration>? retirements = null;
        var topologyChanged = false;
        lock (_nodesGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // The ID is recorded before the migrations are checked, deliberately. IDs are unique
            // per connection, so a repeat is a replay of a notification already evaluated: if
            // its slots were fenced or owned elsewhere then, they still are. Entries waiting on
            // an earlier migration are kept in the deferral list, not by forgetting the ID.
            if (!_smigratedSequences.GetOrCreateValue(item.SequenceScope).TryAdd(item.Notification.SequenceId))
            {
                RecordSmigratedSkipped("duplicate", item.Sender);
                _logger?.LogDebug("Ignored duplicate Cluster SMIGRATED sequence {Sequence} from {Host}:{Port}.",
                    item.Notification.SequenceId, item.Sender.Host, item.Sender.Port);
                return;
            }

            // A notification may list several sources, and the server does not have to send it
            // from the source itself, so the sender is not checked against each source. Each
            // slot moves only if its advertised source owns it now.
            foreach (var (migration, slots) in parsed)
            {
                topologyChanged |= TryApplyMigrationLocked(
                    migration.Source, migration.Target, slots, item.SlotMutationVersion, ref retiredNodes, out var waiting);
                if (waiting is not null)
                    DeferMigrationLocked(new(migration.Source, migration.Target, waiting, item.SlotMutationVersion), item.Sender);
            }
            if (topologyChanged) RetryDeferredMigrationsLocked(ref retiredNodes);

            if (retiredNodes is not null) retirements = RetireInactiveLocked(_redirectVersions.Keys);
        }

        // Launch retirements before the disposal check: DisposeAsync awaits their completion.
        if (retirements is not null)
            foreach (var retirement in retirements) _ = DrainGenerationAsync(retirement);
        if (Volatile.Read(ref _disposed) != 0) return;
        if (retiredNodes is not null)
            foreach (var node in retiredNodes) NodeRetired?.Invoke(node);
        if (topologyChanged) TopologyChanged?.Invoke();
    }

    // A malformed entry is skipped on its own; the other entries still apply. One enumeration
    // budget covers the whole notification, so once it is spent the remaining entries are
    // rejected too: repeated full ranges could otherwise stall the single worker.
    private List<(MaintenanceSlotMigration Migration, int[] Slots)> ParseMigrations(
        QueuedSmigratedNotification item, MaintenanceSlotMigration[] migrations)
    {
        var parsed = new List<(MaintenanceSlotMigration Migration, int[] Slots)>(migrations.Length);
        var budget = ClusterHash.SlotCount;
        for (var i = 0; i < migrations.Length; i++)
        {
            if (TryParseSlots(migrations[i].Slots, ref budget, out var slots))
            {
                parsed.Add((migrations[i], slots));
                continue;
            }
            if (budget < 0)
            {
                RecordSmigratedSkipped("malformed", item.Sender, migrations.Length - i);
                _logger?.LogDebug(
                    "Rejected {Count} Cluster SMIGRATED entries from {Host}:{Port} (sequence {Sequence}): slot ranges exceed {Limit} slots in total.",
                    migrations.Length - i, item.Sender.Host, item.Sender.Port, item.Notification.SequenceId, ClusterHash.SlotCount);
                break;
            }
            RecordSmigratedSkipped("malformed", item.Sender);
            _logger?.LogDebug(
                "Rejected a Cluster SMIGRATED entry from {Host}:{Port} (sequence {Sequence}): invalid slot list {Slots}.",
                item.Sender.Host, item.Sender.Port, item.Notification.SequenceId, migrations[i].Slots);
        }
        return parsed;
    }

    // Caller holds _nodesGate. Moves the slots that the advertised source owns now and that no
    // later owner change has fenced. Returns true when any slot moved. waiting receives the
    // unfenced slots that the source does not own yet (and the target does not already own),
    // or null when there are none.
    private bool TryApplyMigrationLocked(RespireEndpoint sourceEndpoint, RespireEndpoint targetEndpoint, int[] slots,
        long token, ref List<RespireConnectionMultiplexer>? retiredNodes, out int[]? waiting)
    {
        waiting = null;
        if (ClusterNodeIdentityIndex.EndpointsEqual(sourceEndpoint, targetEndpoint)) return false;
        var source = _identities.TryGetExisting(sourceEndpoint, out var existingSource)
            && _identities.IsActive(existingSource) && !existingSource.IsRetired ? existingSource : null;
        var knownTarget = _identities.TryGetExisting(targetEndpoint, out var existingTarget) ? existingTarget : null;

        // Select slots before resolving the target, so a fully fenced migration cannot leave
        // an observed, slotless target transport behind.
        List<int>? movable = null;
        List<int>? pending = null;
        foreach (var slot in slots)
        {
            // A later MOVED, slot clear, discovery change or newer migration owns this slot.
            if (_slotMutationVersions[slot] > token) continue;
            var owner = Volatile.Read(ref _slots[slot]);
            if (owner is not null && ReferenceEquals(owner, source)) (movable ??= []).Add(slot);
            else if (owner is null || !ReferenceEquals(owner, knownTarget)) (pending ??= []).Add(slot);
        }
        waiting = pending?.ToArray();
        if (movable is null) return false;

        var target = _identities.GetOrCreate(targetEndpoint);
        if (ReferenceEquals(source, target) || target.IsRetired)
        {
            waiting = null;
            return false;
        }
        ObserveNode(target);

        // One discovery fence per migration: older in-flight CLUSTER SLOTS replies cannot
        // overwrite these slots.
        var migrationVersion = ++_topologyVersion;
        foreach (var slot in movable)
        {
            PublishSlotLocked(slot, target, migrationVersion);
            // The fence check above already guarantees token >= the stored value. Max keeps the
            // never-decreasing invariant explicit if that check ever changes.
            _slotMutationVersions[slot] = Math.Max(_slotMutationVersions[slot], token);
        }
        AddSlot(target, movable.Count);
        if (RemoveSlot(source!, movable.Count)) (retiredNodes ??= []).Add(source!);
        return true;
    }

    private void DeferMigrationLocked(DeferredSmigratedMigration deferred, RespireConnectionMultiplexer sender)
    {
        _deferredSmigratedMigrations.Add(deferred);
        _deferredSmigratedSlots += deferred.Slots.Length;
        while (_deferredSmigratedMigrations.Count > DeferredSmigratedMigrationLimit
               || _deferredSmigratedSlots > ClusterHash.SlotCount)
        {
            _deferredSmigratedSlots -= _deferredSmigratedMigrations[0].Slots.Length;
            _deferredSmigratedMigrations.RemoveAt(0);
            RecordSmigratedSkipped("deferral_evicted", sender);
        }
    }

    // Retries waiting entries after a migration applied, until a pass makes no progress. Each
    // pass that progresses moves at least one deferred slot, and the list holds at most
    // SlotCount slots, so passes are also capped by the entry limit.
    private void RetryDeferredMigrationsLocked(ref List<RespireConnectionMultiplexer>? retiredNodes)
    {
        for (var pass = 0; pass < DeferredSmigratedMigrationLimit && _deferredSmigratedMigrations.Count > 0; pass++)
        {
            var progressed = false;
            for (var i = 0; i < _deferredSmigratedMigrations.Count; i++)
            {
                var deferred = _deferredSmigratedMigrations[i];
                progressed |= TryApplyMigrationLocked(
                    deferred.Source, deferred.Target, deferred.Slots, deferred.Token, ref retiredNodes, out var waiting);
                _deferredSmigratedSlots -= deferred.Slots.Length - (waiting?.Length ?? 0);
                if (waiting is null) _deferredSmigratedMigrations.RemoveAt(i--);
                else deferred.Slots = waiting;
            }
            if (!progressed) return;
        }
    }

    // Parses "a-b,c,..." into distinct slots. Every range spends its full length from budget,
    // including overlaps, which bounds the work a hostile list can cause. Returns false, with
    // budget left negative, when the budget is exceeded.
    private static bool TryParseSlots(string value, ref int budget, out int[] slots)
    {
        slots = [];
        if (string.IsNullOrEmpty(value)) return false;
        var ranges = new List<(int Start, int End)>();
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
                || end < start || end >= ClusterHash.SlotCount)
                return false;
            if ((budget -= end - start + 1) < 0) return false;
            ranges.Add((start, end));
            if (comma < 0) break;
            remaining = remaining[(comma + 1)..];
            if (remaining.IsEmpty) return false;
        }

        // Merge overlapping and adjacent ranges, then expand once into an exact-size array.
        ranges.Sort();
        var count = 0;
        var merged = 0;
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            if (merged > 0 && start <= ranges[merged - 1].End + 1)
            {
                var previous = ranges[merged - 1];
                if (end > previous.End)
                {
                    count += end - previous.End;
                    ranges[merged - 1] = (previous.Start, end);
                }
                continue;
            }
            ranges[merged++] = (start, end);
            count += end - start + 1;
        }

        slots = new int[count];
        var index = 0;
        for (var i = 0; i < merged; i++)
            for (var slot = ranges[i].Start; slot <= ranges[i].End; slot++)
                slots[index++] = slot;
        return slots.Length > 0;
    }
}
