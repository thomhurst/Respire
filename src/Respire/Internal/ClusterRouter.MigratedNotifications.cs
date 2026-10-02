using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;
using MigrationState = Respire.Internal.ClusterMigrationState<Respire.Infrastructure.RespireConnectionMultiplexer>;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private const int SmigratedQueueCapacity = 128;
    private const long SmigratedDropWarningIntervalMilliseconds = 30_000;
    // SequenceScope is the physical connection that received the push. Sequence IDs restart
    // with each connection, so deduplication never spans a reconnect, and items queued by an
    // old connection cannot mark a new connection's IDs as already seen.
    // SlotMutationVersion is the ClusterSlotMutationClock value read when the push was identified.
    internal sealed record QueuedSmigratedNotification(
        RespireConnectionMultiplexer Sender, object SequenceScope,
        MaintenanceNotification Notification, long SlotMutationVersion);

    private readonly Channel<QueuedSmigratedNotification> _smigratedNotifications;
    private readonly Dictionary<RespireConnectionMultiplexer, MaintenanceNotificationHandler> _nodeMaintenanceHandlers = [];
    private readonly MigrationState _migrations;
    // Started on the first queued notification, so routers that never see SMIGRATED own no task.
    // DisposeAsync swaps in a completed task, after which no worker can start.
    private Task? _smigratedWorker;
    private long _smigratedNotificationsDropped;
    private long _pendingSmigratedDropDiagnostics;
    private int _smigratedDropDiagnosticsQueued;
    private long _lastSmigratedDropWarning = long.MinValue;
    // Set only while the worker runs ApplySmigratedNotification and its callbacks. Being
    // thread-static, it does not flow into tasks a callback starts, so their disposal still
    // joins the worker. ApplySmigratedNotification must therefore stay synchronous: an await
    // inside it would resume without the marker. ClientCore captures it before its own awaits.
    [ThreadStatic]
    private static ClusterRouter? _smigratedWorkerContext;

    internal bool IsOnSmigratedWorker => ReferenceEquals(_smigratedWorkerContext, this);

    internal long SmigratedNotificationsDropped => Interlocked.Read(ref _smigratedNotificationsDropped);

    internal int SmigratedDropDiagnosticsQueued => Volatile.Read(ref _smigratedDropDiagnosticsQueued);

    // Test seam for observing a queued discovery request without reflecting router fields.
    internal ClusterTopologyRefreshScheduler.Decision NextTopologyRefresh() => _topologyRefresh.Next();

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private Channel<QueuedSmigratedNotification> CreateSmigratedChannel()
        => Channel.CreateBounded<QueuedSmigratedNotification>(new BoundedChannelOptions(SmigratedQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        }, OnSmigratedNotificationDropped);

    // Runs on the receive loop that overflowed the queue. Only update counters there;
    // metric and logger callbacks can re-enter client disposal and must run off the receive loop.
    // A single queued drain coalesces bursts, so slow diagnostics cannot build an unbounded
    // thread-pool backlog or retain every dropped notification.
    private void OnSmigratedNotificationDropped(QueuedSmigratedNotification dropped)
    {
        Interlocked.Increment(ref _smigratedNotificationsDropped);
        Interlocked.Increment(ref _pendingSmigratedDropDiagnostics);
        if (Interlocked.CompareExchange(ref _smigratedDropDiagnosticsQueued, 1, 0) != 0) return;
        if (!ThreadPool.UnsafeQueueUserWorkItem(
                static router => router.ReportPendingSmigratedDropDiagnostics(), this, preferLocal: false))
            Volatile.Write(ref _smigratedDropDiagnosticsQueued, 0);
    }

    private void ReportPendingSmigratedDropDiagnostics()
    {
        while (true)
        {
            var count = Interlocked.Exchange(ref _pendingSmigratedDropDiagnostics, 0);
            if (count > 0)
            {
                // Lost migrations need discovery, using the same debounce and failure backoff as MOVED.
                SignalMovedTopologyRefresh();
                ReportSmigratedNotificationDrop(count);
            }

            Volatile.Write(ref _smigratedDropDiagnosticsQueued, 0);
            if (Interlocked.Read(ref _pendingSmigratedDropDiagnostics) == 0
                || Interlocked.CompareExchange(ref _smigratedDropDiagnosticsQueued, 1, 0) != 0)
                return;
        }
    }

    private void ReportSmigratedNotificationDrop(long count)
    {
        try
        {
            if (RespireTelemetry.ClusterSlotMigrationsSkipped.Enabled)
                RespireTelemetry.ClusterSlotMigrationsSkipped.Add(count,
                    new KeyValuePair<string, object?>("reason", "queue_full"));
        }
        catch (Exception error)
        {
            try { _options.LoggerFactory?.CreateLogger("Respire.Cluster").LogError(error, "Error recording clustered migration-drop metric."); }
            catch { /* A failing logger must not abandon the queue diagnostics drain. */ }
        }
        if (_logger is null) return;
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastSmigratedDropWarning);
        if ((last != long.MinValue && now - last < SmigratedDropWarningIntervalMilliseconds)
            || Interlocked.CompareExchange(ref _lastSmigratedDropWarning, now, last) != last) return;
        try
        {
            _logger.LogWarning(
                "Cluster SMIGRATED queue is full; {Dropped} notifications dropped so far. MOVED handling and topology discovery will correct the affected slots.",
                SmigratedNotificationsDropped);
        }
        catch
        {
            // A failing logger is an isolated diagnostic listener.
        }
    }

    // Called on diagnostic and migration workers, where a throw could abandon a half-applied
    // notification. Listener failures are isolated as in the maintenance diagnostics path.
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
        // Claim the sequence before applying: a replay must not gain authority later if this
        // notification was malformed, fenced, or waiting for its source to own the slots.
        if (!_migrations.TryRecordSequence(item.SequenceScope, item.Notification.SequenceId))
        {
            RecordSmigratedSkipped("duplicate", item.Sender);
            _logger?.LogDebug("Ignored duplicate Cluster SMIGRATED sequence {Sequence} from {Host}:{Port}.",
                item.Notification.SequenceId, item.Sender.Host, item.Sender.Port);
            return;
        }
        var parsed = ParseMigrations(item, migrations);

        List<RespireConnectionMultiplexer>? retiredNodes = null;
        List<RespireConnectionMultiplexer>? retiredReplicas = null;
        List<RetiredGeneration>? retirements = null;
        var skippedMetrics = new List<(string Reason, RespireConnectionMultiplexer Sender)>();
        var topologyChanged = false;
        long topologyVersion = 0;
        RespireEndpoint[]? topologyEndpoints = null;
        bool topologyAuthoritative = false;
        lock (_nodesGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // A notification may list several sources, and the server does not have to send it
            // from the source itself, so the sender is not checked against each source. Each
            // slot moves only if its advertised source owns it now.
            _migrations.Expire(skippedMetrics);
            Queue<MigrationState.AppliedMove>? applied = null;
            foreach (var (migration, slots) in parsed)
            {
                if (TryApplyMigrationLocked(migration.Source, migration.Target, slots, item.SlotMutationVersion,
                        ref retiredNodes, out var waiting) is { } move)
                {
                    topologyChanged = true;
                    (applied ??= new()).Enqueue(move);
                }
                if (waiting is not null)
                    _migrations.Defer(new(migration.Source, migration.Target, waiting, item.SlotMutationVersion,
                        _migrations.Timestamp, item.Sender), skippedMetrics);
            }
            if (applied is not null && _migrations.DeferredCount != 0)
                _migrations.RetryDependencies(applied,
                    endpoint => _identities.TryGetExisting(endpoint, out var source) ? source : null,
                    (RespireEndpoint source, RespireEndpoint target, int[] slots, long token, out int[]? waiting)
                        => TryApplyMigrationLocked(source, target, slots, token, ref retiredNodes, out waiting));

            if (topologyChanged)
            {
                retiredReplicas = RemoveUnroutedReplicasLocked();
                retirements = RetireInactiveLocked(_redirectVersions.Keys);
                topologyVersion = _topologyVersion;
                topologyEndpoints = _masters.Where((master, index) => _masterSlotCounts[index] != 0 && !master.IsRetired)
                    .Select(static master => Endpoint(master)).Distinct().ToArray();
                topologyAuthoritative = HasCompleteTopology();
            }
        }

        // Launch retirements before the disposal check and before any listener runs:
        // DisposeAsync awaits their completion, so a metric listener that disposes the client
        // synchronously would otherwise wait for a drain that this thread has not started yet.
        if (retirements is not null)
            foreach (var retirement in retirements) _ = DrainGenerationAsync(retirement);
        foreach (var (reason, sender) in skippedMetrics) RecordSmigratedSkipped(reason, sender);
        if (Volatile.Read(ref _disposed) != 0) return;
        if (retiredNodes is not null)
            foreach (var node in retiredNodes) NodeRetired?.Invoke(node);
        if (retiredReplicas is not null)
            foreach (var node in retiredReplicas) ReplicaNodeRetired?.Invoke(node);
        if (topologyEndpoints is not null) TopologyChanged?.Invoke(topologyVersion, topologyEndpoints, topologyAuthoritative);
    }

    // Caller holds _nodesGate. Shared replica sets can still serve another slot range.
    private List<RespireConnectionMultiplexer>? RemoveUnroutedReplicasLocked()
    {
        if (_replicaNodes.Length == 0) return null;
        var active = new HashSet<RespireConnectionMultiplexer>();
        var sets = new HashSet<ClusterReplicaSet>();
        foreach (var routes in _replicasBySlot)
            if (routes is not null && sets.Add(routes)) active.UnionWith(routes.Nodes);
        List<RespireConnectionMultiplexer>? retired = null;
        foreach (var node in _replicaNodes)
            if (!active.Contains(node)) (retired ??= []).Add(node);
        if (retired is not null) Volatile.Write(ref _replicaNodes, active.ToArray());
        return retired;
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
    private MigrationState.AppliedMove? TryApplyMigrationLocked(RespireEndpoint sourceEndpoint, RespireEndpoint targetEndpoint,
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
        foreach (var slot in movable)
        {
            PublishSlotLocked(slot, target, migrationVersion);
            // The source shard's replicas cannot serve the migrated slot or its pinned cursors.
            Volatile.Write(ref _replicasBySlot[slot], null);
            _unknownReplicaRoutes.TryRemove(slot, out _);
        }
        _slotFences.RecordMigration(movable, source!, sourceEndpoint, target, targetEndpoint, token);
        AddSlot(target, movable.Count);
        if (RemoveSlot(source!, movable.Count, preserveMaintenanceHandlerForRetirement: true))
            (retiredNodes ??= []).Add(source!);
        return new MigrationState.AppliedMove(target, [.. movable]);
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

