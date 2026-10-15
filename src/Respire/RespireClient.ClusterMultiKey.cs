using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private bool CanSplitClusterCommand(string operation)
        => _core.Cluster is not null && _core.Options.SplitCrossSlotCommands
            && operation is "MGET" or "MSET" or "DEL" or "UNLINK" or "EXISTS" or "TOUCH";

    // Scan without allocating on the same-slot path. Only a cross-slot request needs a plan.
    private ClusterPartition[]? PlanClusterPartitions<TCommand>(string operation, in TCommand command)
        where TCommand : struct, IRespCommand
    {
        // MGetCommand already carries the slot validated by the per-key cache path.
        if (typeof(TCommand) == typeof(PartitionCommand) || typeof(TCommand) == typeof(MGetCommand)
            || !CanSplitClusterCommand(operation)
            || !command.TryGetClientCacheKey(operation, out var arguments)) return null;
        var stride = operation == "MSET" ? 2 : 1;
        if (arguments.ArgumentCount % stride != 0)
            throw new ArgumentException("MSET requires key/value pairs.", "args");
        int? firstSlot = null;
        var crossSlot = false;
        var count = 0;
        while (count < arguments.ArgumentCount)
        {
            var key = arguments.GetArgument(count);
            RespireValue.ThrowIfNull(key, "keys");
            key.TryGetClusterSlot(out var slot);
            firstSlot ??= slot;
            crossSlot |= slot != firstSlot;
            count += stride;
        }
        if (!crossSlot) return null;

        var groups = new Dictionary<int, ClusterPartition>();
        for (var index = 0; index < count; index += stride)
        {
            var key = arguments.GetArgument(index).Snapshot();
            key.TryGetClusterSlot(out var slot);
            if (!groups.TryGetValue(slot, out var group))
                groups.Add(slot, group = new(slot));
            group.Indexes.Add(index / stride);
            group.Arguments.Add(key);
            if (stride == 2)
            {
                group.Arguments.Add(arguments.GetArgument(index + 1).Snapshot());
            }
        }
        return groups.Values.ToArray();
    }

    private ClusterPartition[]? PlanMGetPartitions(ReadOnlySpan<RespireKey> keys, bool keysResolved)
    {
        if (!CanSplitClusterCommand("MGET") || keys.Length < 2) return null;
        var first = keysResolved ? keys[0] : ResolveKey(keys[0]);
        var firstSlot = first.ClusterSlot;
        for (var index = 1; index < keys.Length; index++)
        {
            var key = keysResolved ? keys[index] : ResolveKey(keys[index]);
            if (key.ClusterSlot == firstSlot) continue;
            var arguments = new RespireValue[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                arguments[i] = (keysResolved ? keys[i] : ResolveKey(keys[i])).AsValue();
            return PlanClusterPartitions("MGET", new CmdN(Verbs.MGet, arguments));
        }
        return null;
    }

    private sealed class ClusterPartition(int slot)
    {
        internal int Slot { get; } = slot;
        internal List<int> Indexes { get; } = [];
        internal List<RespireValue> Arguments { get; } = [];
    }

    // Preserve the original read/retry/cache declarations (including raw caller overrides).
    // Admission-tracked calls release their gate only after the entire fan-out completes.
    private readonly struct PartitionCommand(Verb verb, RespireValue[] arguments, int slot,
        ReadCommandKind readKind, RespireCommandRetryCategory retryCategory, RespireCacheMutation mutation,
        ClientCacheCommandMetadata metadata) : IRespCommand
    {
        public ReadCommandKind ReadKind => readKind;
        public RespireCommandRetryCategory RetryCategory => retryCategory;
        public RespireCacheMutation GetCacheMutation(string operation) => mutation;
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => metadata;
        public bool TryGetPrimaryKey(out RespireValue key) { key = arguments[0]; return true; }
        public bool TryGetClusterSlot(out int value) { value = slot; return true; }
        public bool TryGetArgument(int index, out RespireValue value)
        {
            value = (uint)index < (uint)arguments.Length ? arguments[index] : default;
            return (uint)index < (uint)arguments.Length;
        }
        public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
        { key = new(operation, arguments); return true; }
        public void Write(ref RespWriter writer) => new CmdN(verb, arguments).Write(ref writer);
        public int GetWriteSizeHint() => new CmdN(verb, arguments).GetWriteSizeHint();
    }

    private async ValueTask<TResult> ConvertClusterPartitionsAsync<TCommand, TState, TResult>(string operation,
        TCommand command, ClusterPartition[] groups, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, TResult> converter, bool transferOwnership,
        RespireTelemetry.ErrorObservation observation, bool cachedMGet = false)
        where TCommand : struct, IRespCommand
    {
        var response = await SendClusterPartitionsAsync(operation, command, groups, cancellationToken,
            RespireCommandFlags.None, allowReadFrom: true, observation, cachedMGet).ConfigureAwait(false);
        var transferred = false;
        try
        {
            var result = converter(state, in response);
            transferred = transferOwnership;
            return result;
        }
        finally { if (!transferred) response.Dispose(); }
    }

    private async ValueTask<RespValue> SendClusterPartitionsAsync<TCommand>(string operation, TCommand command,
        ClusterPartition[] groups, CancellationToken cancellationToken, RespireCommandFlags flags,
        bool allowReadFrom, RespireTelemetry.ErrorObservation observation, bool cachedMGet = false)
        where TCommand : struct, IRespCommand
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var verb = operation switch
        {
            "MGET" => Verbs.MGet, "MSET" => Verbs.MSet, "DEL" => Verbs.Del,
            "UNLINK" => Verbs.Unlink, "EXISTS" => Verbs.Exists, "TOUCH" => Verbs.Touch,
            _ => throw new InvalidOperationException("Unsupported split command."),
        };
        var legs = RespireTelemetry.FanOutLegObservation.Create(groups.Length, observation);
        var tasks = new Task<RespValue>[groups.Length];
        for (var index = 0; index < groups.Length; index++)
            tasks[index] = SendClusterPartitionAsync(operation, command, verb, groups[index], cancellationToken,
                flags, allowReadFrom, legs[index], cachedMGet);

        try
        {
            var replies = await RespireTelemetry.FanOutLegObservation.JoinAsync(tasks, legs, observation).ConfigureAwait(false);
            if (operation == "MGET")
            {
                var values = new RespValue[groups.Sum(static group => group.Indexes.Count)];
                for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                {
                    var reply = replies[groupIndex].AsArray();
                    for (var index = 0; index < reply.Length; index++)
                        values[groups[groupIndex].Indexes[index]] = reply[index].ToOwned();
                }
                return RespValue.Array(values);
            }
            if (operation == "MSET") return RespValue.SimpleString("OK"u8.ToArray());
            long total = 0;
            foreach (var reply in replies) total = checked(total + ResponseReader.Integer(in reply));
            return RespValue.Integer(total);
        }
        finally
        {
            // WhenAll joins failures too: no successful pooled reply or late fault is abandoned.
            foreach (var task in tasks)
                if (task.IsCompletedSuccessfully) (await task.ConfigureAwait(false)).Dispose();
            command.OnAccepted();
        }
    }

    private async Task<RespValue> SendClusterPartitionAsync<TCommand>(string operation, TCommand original, Verb verb,
        ClusterPartition group, CancellationToken cancellationToken, RespireCommandFlags flags, bool allowReadFrom,
        RespireTelemetry.FanOutLegObservation leg, bool cachedMGet)
        where TCommand : struct, IRespCommand
    {
        var response = default(RespValue);
        try
        {
            var arguments = group.Arguments.ToArray();
            if (cachedMGet)
            {
                var keys = Array.ConvertAll(arguments, static argument => argument.AsKey());
                var values = await CachedGetManyUnhookedAsync(keys, cancellationToken,
                    static (RespireClient _, in RespValue value) => value.ToOwned(), keysResolved: true,
                    observeErrors: false, observation: leg.Observation).ConfigureAwait(false);
                response = RespValue.Array(values);
            }
            else
            {
                response = await SendCoreAsync(operation, new PartitionCommand(verb, arguments, group.Slot,
                        original.ReadKind, original.RetryCategory, original.GetCacheMutation(operation), original.GetClientCacheMetadata(operation)),
                    cancellationToken, flags, allowReadFrom, cursorAffinity: null, observation: leg.Observation,
                    observeErrors: false).ConfigureAwait(false);
            }
            if (operation == "MGET" && response.AsArray().Length != group.Indexes.Count)
                throw new RespireProtocolException("MGET returned an unexpected number of values.");
            if (operation == "MSET") ResponseReader.ExpectOk(in response);
            if (operation is not ("MGET" or "MSET")) _ = ResponseReader.Integer(in response);
            return response;
        }
        catch (OperationCanceledException)
        {
            response.Dispose();
            throw;
        }
        catch (Exception error)
        {
            response.Dispose();
            throw new RespireClusterPartitionException(operation, group.Slot,
                error is RespireException { HasAmbiguousEndpoint: true } ? null
                    : (error as RespireException)?.Endpoint ?? leg.LegEndpoint, error);
        }
    }
}
