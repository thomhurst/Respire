using System.Diagnostics;
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
    // A dependency normally arrives within milliseconds. An entry still waiting after this long
    // is stale: applying it if its source later regains the slots would be wrong.
    private const long DeferredSmigratedLifetimeMilliseconds = 30_000;

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
    // (B->C before A->B). The entry is retried when a migration moves its slots to its source,
    // under its original fence token, so a MOVED, discovery change or newer migration still
    // rejects it. Entries expire after DeferredSmigratedLifetimeMilliseconds. Slots are sorted.
    private sealed class DeferredSmigratedMigration(
        RespireEndpoint source, RespireEndpoint target, int[] slots, long token, long deferredAt)
    {
        internal readonly RespireEndpoint Source = source;
        internal readonly RespireEndpoint Target = target;
        internal readonly long Token = token;
        internal readonly long DeferredAt = deferredAt;
        internal int[] Slots = slots;
    }

    // A migration that just moved Slots (sorted ascending) to Target.
    private readonly record struct AppliedSmigratedMove(RespireConnectionMultiplexer Target, int[] Slots);
    private readonly record struct SmigratedDropWorkItem(
        ClusterRouter Router, QueuedSmigratedNotification Notification);

    private readonly Channel<QueuedSmigratedNotification> _smigratedNotifications;
    private readonly Dictionary<RespireConnectionMultiplexer, MaintenanceNotificationHandler> _nodeMaintenanceHandlers = [];
    // Accessed under _smigratedSequenceGate. A window disappears with its connection.
    private readonly ConditionalWeakTable<object, SmigratedSequenceWindow> _smigratedSequences = new();
    private readonly object _smigratedSequenceGate = new();
    // Accessed only under _nodesGate. Oldest first; bounded by entry count and total slots.
    private readonly List<DeferredSmigratedMigration> _deferredSmigratedMigrations = [];
    private int _deferredSmigratedSlots;
    // Test seam: the millisecond clock that ages deferred entries.
    internal Func<long> SmigratedClock { get; set; } = static () => Environment.TickCount64;
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

    // Runs on the receive loop that overflowed the queue. Only update the local count there;
    // metric and logger callbacks can re-enter client disposal and must run off the receive loop.
    private void OnSmigratedNotificationDropped(QueuedSmigratedNotification dropped)
    {
        Interlocked.Increment(ref _smigratedNotificationsDropped);
        ThreadPool.UnsafeQueueUserWorkItem(
            static work => work.Router.ReportSmigratedNotificationDrop(work.Notification),
            new SmigratedDropWorkItem(this, dropped), preferLocal: false);
    }

    private void ReportSmigratedNotificationDrop(QueuedSmigratedNotification dropped)
    {
        RecordSmigratedSkipped("queue_full", dropped.Sender);
        if (_logger is null) return;
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastSmigratedDropWarning);
        if ((last != long.MinValue && now - last < SmigratedDropWarningIntervalMilliseconds)
            || Interlocked.CompareExchange(ref _lastSmigratedDropWarning, now, last) != last) return;
        try
        {
            _logger.LogWarning(
                "Cluster SMIGRATED queue is full; dropped the oldest notification (from {Host}:{Port}). {Dropped} dropped so far. MOVED handling and topology discovery will correct the affected slots.",
                dropped.Sender.Host, dropped.Sender.Port, SmigratedNotificationsDropped);
        }
        catch
        {
            // A failing logger is an isolated diagnostic listener.
        }
    }

    // Called on the receive loop (queue drops) and by the worker under _nodesGate, where a
    // throw would abandon a half-applied notification. Listener failures are therefore
    // contained here, as the maintenance diagnostics path contains them.
    private void RecordSmigratedSkipped(string reason, RespireConnectionMultiplexer sender, long count = 1)
    {
        try
        {
            if (!RespireTelemetry.ClusterSlotMigrationsSkipped.Enabled) return;
            RespireTelemetry.ClusterSlotMigrationsSkipped.Add(count,
                new KeyValuePair<string, object?>("reason", reason),
                new KeyValuePair<string, object?>("server.address", sender.Host),
                new KeyValuePair<string, object?>("server.port", sender.Port));
        }
        catch (Exception error)
        {
            try { _logger?.LogWarning(error, "Cluster slot migration metric listener threw."); }
            catch { /* A failing logger is also an isolated diagnostic listener. */ }
        }
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
    // The fence token was read by the receive loop as soon as it identified the SMIGRATED push,
    // before parsing it, so an owner change made since then is newer than this item.
    private void QueueSmigratedNotification(RespireConnectionMultiplexer sender, object sequenceScope,
        MaintenanceNotification notification, long slotMutationToken)
    {
        if (!notification.IsSlotMigration || Volatile.Read(ref _disposed) != 0
            || notification.Migrations is not { Length: > 0 }) return;
        EnsureSmigratedWorker();
        // The receive loop captured this callback while the sender was active, so a push that
        // arrived before the sender retired still lands here. Preserve that receive-time
        // validity: an earlier FIFO item can retire the sender before a later queued item runs.
        _smigratedNotifications.Writer.TryWrite(new(sender, sequenceScope, notification, slotMutationToken));
    }

    // Test seam: stamps a notification as received now.
    internal QueuedSmigratedNotification CaptureSmigratedNotification(
        RespireConnectionMultiplexer sender, object sequenceScope, MaintenanceNotification notification)
        => new(sender, sequenceScope, notification, ClusterSlotMutationClock.Next());

    private void MarkSlotMutatedLocked(int slot) => _slotFences.MarkOwnerChanged(slot);

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
                // Fails if ApplySmigratedNotification or a callback ever resumes on another
                // thread (an await crept in): disposal from that callback would then self-join.
                Debug.Assert(ReferenceEquals(_smigratedWorkerContext, this),
                    "ApplySmigratedNotification must stay synchronous to keep the worker marker.");
            }
            catch (Exception error)
            {
                // A throwing logger must not escape: it would fault the only worker, and every
                // later notification would then sit in the queue until it was dropped.
                if (Volatile.Read(ref _disposed) == 0)
                {
                    try
                    {
                        _logger?.LogError(error, "Failed to apply Cluster SMIGRATED notification from {Host}:{Port}.",
                            item.Sender.Host, item.Sender.Port);
                    }
                    catch
                    {
                        // A failing logger is an isolated diagnostic listener.
                    }
                }
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
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!TryRecordSmigratedSequence(item))
        {
            RecordSmigratedSkipped("duplicate", item.Sender);
            _logger?.LogDebug("Ignored duplicate Cluster SMIGRATED sequence {Sequence} from {Host}:{Port}.",
                item.Notification.SequenceId, item.Sender.Host, item.Sender.Port);
            return;
        }
        var parsed = ParseMigrations(item, migrations);

        List<RespireConnectionMultiplexer>? retiredNodes = null;
        List<RetiredGeneration>? retirements = null;
        var skippedMetrics = new List<(string Reason, long Count)>();
        var topologyChanged = false;
        lock (_nodesGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // A notification may list several sources, and the server does not have to send it
            // from the source itself, so the sender is not checked against each source. Each
            // slot moves only if its advertised source owns it now.
            ExpireDeferredMigrationsLocked(skippedMetrics);
            Queue<AppliedSmigratedMove>? applied = null;
            foreach (var (migration, slots) in parsed)
            {
                if (TryApplyMigrationLocked(migration.Source, migration.Target, slots, item.SlotMutationVersion,
                        ref retiredNodes, out var waiting) is { } move)
                {
                    topologyChanged = true;
                    (applied ??= new()).Enqueue(move);
                }
                if (waiting is not null)
                    DeferMigrationLocked(new(migration.Source, migration.Target, waiting, item.SlotMutationVersion,
                        SmigratedClock()), skippedMetrics);
            }
            if (applied is not null) RetryDependentMigrationsLocked(applied, ref retiredNodes);

            if (retiredNodes is not null) retirements = RetireInactiveLocked(_redirectVersions.Keys);
        }

        // Launch retirements before the disposal check and before any listener runs:
        // DisposeAsync awaits their completion, so a metric listener that disposes the client
        // synchronously would otherwise wait for a drain that this thread has not started yet.
        if (retirements is not null)
            foreach (var retirement in retirements) _ = DrainGenerationAsync(retirement);
        foreach (var (reason, count) in skippedMetrics) RecordSmigratedSkipped(reason, item.Sender, count);
        if (Volatile.Read(ref _disposed) != 0) return;
        if (retiredNodes is not null)
            foreach (var node in retiredNodes) NodeRetired?.Invoke(node);
        if (topologyChanged) TopologyChanged?.Invoke();
    }

    private bool TryRecordSmigratedSequence(QueuedSmigratedNotification item)
    {
        lock (_smigratedSequenceGate)
            return _smigratedSequences.GetOrCreateValue(item.SequenceScope)
                .TryAdd(item.Notification.SequenceId);
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
                TryLogMalformedMigration(
                    "Rejected {Count} Cluster SMIGRATED entries from {Host}:{Port} (sequence {Sequence}): slot ranges exceed {Limit} slots in total.",
                    migrations.Length - i, item.Sender.Host, item.Sender.Port, item.Notification.SequenceId, ClusterHash.SlotCount);
                break;
            }
            RecordSmigratedSkipped("malformed", item.Sender);
            TryLogMalformedMigration(
                "Rejected a Cluster SMIGRATED entry from {Host}:{Port} (sequence {Sequence}): invalid slot list {Slots}.",
                item.Sender.Host, item.Sender.Port, item.Notification.SequenceId, migrations[i].Slots);
        }
        return parsed;
    }

    private void TryLogMalformedMigration(string message, params object?[] args)
    {
        try { _logger?.LogDebug(message, args); }
        catch { /* Diagnostic providers cannot discard valid entries from this notification. */ }
    }

    // Caller holds _nodesGate. Moves the slots that the advertised source owns now and that no
    // later owner change has fenced, and returns the move, or null when no slot moved. waiting
    // receives the unfenced slots that the source does not own yet (and the target does not
    // already own), in ascending order, or null when there are none.
    private AppliedSmigratedMove? TryApplyMigrationLocked(RespireEndpoint sourceEndpoint, RespireEndpoint targetEndpoint,
        int[] slots, long token, ref List<RespireConnectionMultiplexer>? retiredNodes, out int[]? waiting)
    {
        waiting = null;
        if (ClusterNodeIdentityIndex.EndpointsEqual(sourceEndpoint, targetEndpoint)) return null;
        var source = _identities.TryGetExisting(sourceEndpoint, out var existingSource)
            && _identities.IsActive(existingSource) && !existingSource.IsRetired ? existingSource : null;
        var knownTarget = _identities.TryGetExisting(targetEndpoint, out var existingTarget) ? existingTarget : null;

        // Select slots before resolving the target, so a fully fenced migration cannot leave
        // an observed, slotless target transport behind.
        List<int>? movable = null;
        List<int>? pending = null;
        foreach (var slot in slots)
        {
            var owner = Volatile.Read(ref _slots[slot]);
            if (_slotFences.IsFenced(slot, owner, source, sourceEndpoint, token)) continue;
            if (owner is not null && ReferenceEquals(owner, source)) (movable ??= []).Add(slot);
            else if (owner is null || !ReferenceEquals(owner, knownTarget)) (pending ??= []).Add(slot);
        }
        waiting = pending?.ToArray();
        if (movable is null) return null;

        var target = _identities.GetOrCreate(targetEndpoint);
        if (ReferenceEquals(source, target) || target.IsRetired)
        {
            waiting = null;
            return null;
        }
        ObserveNode(target);

        // One discovery fence per migration: older in-flight CLUSTER SLOTS replies cannot
        // overwrite these slots.
        var migrationVersion = ++_topologyVersion;
        foreach (var slot in movable) PublishSlotLocked(slot, target, migrationVersion);
        _slotFences.RecordMigration(movable, source!, sourceEndpoint, target, targetEndpoint, token);
        AddSlot(target, movable.Count);
        if (RemoveSlot(source!, movable.Count)) (retiredNodes ??= []).Add(source!);
        return new AppliedSmigratedMove(target, [.. movable]);
    }

    private void DeferMigrationLocked(DeferredSmigratedMigration deferred,
        List<(string Reason, long Count)> skippedMetrics)
    {
        _deferredSmigratedMigrations.Add(deferred);
        _deferredSmigratedSlots += deferred.Slots.Length;
        while (_deferredSmigratedMigrations.Count > DeferredSmigratedMigrationLimit
               || _deferredSmigratedSlots > ClusterHash.SlotCount)
        {
            _deferredSmigratedSlots -= _deferredSmigratedMigrations[0].Slots.Length;
            _deferredSmigratedMigrations.RemoveAt(0);
            skippedMetrics.Add(("deferral_evicted", 1));
        }
    }

    // Drops entries whose dependency has not arrived within DeferredSmigratedLifetimeMilliseconds.
    // The list is oldest first, so expiry stops at the first entry that is still young.
    private void ExpireDeferredMigrationsLocked(List<(string Reason, long Count)> skippedMetrics)
    {
        if (_deferredSmigratedMigrations.Count == 0) return;
        var now = SmigratedClock();
        var expired = 0;
        while (expired < _deferredSmigratedMigrations.Count
               && now - _deferredSmigratedMigrations[expired].DeferredAt >= DeferredSmigratedLifetimeMilliseconds)
            _deferredSmigratedSlots -= _deferredSmigratedMigrations[expired++].Slots.Length;
        if (expired == 0) return;
        _deferredSmigratedMigrations.RemoveRange(0, expired);
        skippedMetrics.Add(("deferral_expired", expired));
    }

    // Retries only the entries a move made runnable: those whose source is the node that just
    // received some of their slots. Only an SMIGRATED move can unblock an entry, because MOVED,
    // slot clears and discovery reset the slot's chain and fence it. Moves made here are queued
    // in turn, so chains resolve in one call. Each retry removes slots from the list, so the
    // work under _nodesGate is bounded by the list size rather than by repeated full passes.
    private void RetryDependentMigrationsLocked(Queue<AppliedSmigratedMove> applied,
        ref List<RespireConnectionMultiplexer>? retiredNodes)
    {
        while (_deferredSmigratedMigrations.Count > 0 && applied.TryDequeue(out var move))
        {
            for (var i = 0; i < _deferredSmigratedMigrations.Count; i++)
            {
                var deferred = _deferredSmigratedMigrations[i];
                if (!_identities.TryGetExisting(deferred.Source, out var source) || !ReferenceEquals(source, move.Target))
                    continue;
                var ready = SplitSortedSlots(deferred.Slots, move.Slots, out var remaining);
                if (ready is null) continue;

                if (TryApplyMigrationLocked(deferred.Source, deferred.Target, ready, deferred.Token,
                        ref retiredNodes, out var waiting) is { } next)
                    applied.Enqueue(next);
                // Slots that another entry moved first keep waiting.
                if (waiting is not null) remaining = MergeSortedSlots(remaining, waiting);
                _deferredSmigratedSlots -= deferred.Slots.Length - remaining.Length;
                if (remaining.Length == 0) _deferredSmigratedMigrations.RemoveAt(i--);
                else deferred.Slots = remaining;
            }
        }
    }

    // Splits sorted slots into those also in sorted moved (returned, or null when none) and the rest.
    private static int[]? SplitSortedSlots(int[] slots, int[] moved, out int[] rest)
    {
        List<int>? common = null;
        var others = new List<int>(slots.Length);
        var j = 0;
        foreach (var slot in slots)
        {
            while (j < moved.Length && moved[j] < slot) j++;
            if (j < moved.Length && moved[j] == slot) (common ??= []).Add(slot);
            else others.Add(slot);
        }
        rest = common is null ? slots : [.. others];
        return common?.ToArray();
    }

    private static int[] MergeSortedSlots(int[] first, int[] second)
    {
        var merged = new int[first.Length + second.Length];
        first.CopyTo(merged, 0);
        second.CopyTo(merged, first.Length);
        Array.Sort(merged);
        return merged;
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
