using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // A reconnect or redirect must discover the destination's capabilities independently.
    // Weak keys neither retain retired sockets nor put lock state on the transport hot path.
    private static readonly ConditionalWeakTable<RespireConnection, LockCapabilities> LockConnectionCapabilities = new();

    [Flags]
    private enum LockCapability { ConditionalSet = 1, Delex = 2, Delifeq = 4 }

    private sealed class LockCapabilities
    {
        private int _unsupported;

        internal bool CanTry(LockCapability capability) => (Volatile.Read(ref _unsupported) & (int)capability) == 0;
        internal void Reject(LockCapability capability) => Interlocked.Or(ref _unsupported, (int)capability);
    }

    internal sealed class TrackedLockExecution(TrackedConnectionIdentity connectionIdentity)
    {
        internal TrackedConnectionIdentity ConnectionIdentity { get; set; } = connectionIdentity;
        internal ValueTask<bool> Response { get; set; }

        /// <summary>
        /// The single submission state for a lock command: whether it may have been written and is
        /// still unanswered. The routing loop sets it before each send and clears it on proof that
        /// the attempt never ran: MOVED, ASK, a retired connection, or a transport report that the
        /// command was not enqueued (see <see cref="LockCommands.IsUnsubmitted"/>). A failure
        /// while obtaining the next connection therefore keeps it cleared. Read only after
        /// <see cref="Response"/> completes.
        /// </summary>
        internal bool CommandMayBeOutstanding { get; set; }
    }

    private int _unfencedLockReleaseLogged;

    /// <summary>
    /// Logs once per client that lock releases run without the <c>CLIENT KILL</c> fence because
    /// the server or ACL denied <c>CLIENT ID</c> or <c>CLIENT KILL</c>. Releases still fail closed;
    /// only the fence that stops a latent delete is unavailable.
    /// </summary>
    internal void LogUnfencedLockReleaseOnce(RespireServerException error)
    {
        if (Interlocked.Exchange(ref _unfencedLockReleaseLogged, 1) == 0)
        {
            _core.Logger?.LogWarning(error,
                "Lock releases run without connection fencing because CLIENT ID or CLIENT KILL was denied. " +
                "An uncertain release still treats ownership as lost, but its delete may run later. " +
                "Grant the client and client|id/client|kill permissions to restore fencing.");
        }
    }

    /// <summary>
    /// Fences the connection that carried an uncertain lock command without letting a fence
    /// failure replace the caller's original error. A failure is logged, because the latent
    /// command may still run after the caller has treated ownership as lost.
    /// </summary>
    internal async ValueTask TryFenceLockConnectionAsync(TrackedConnectionIdentity identity, string operation)
    {
        try
        {
            await FenceCorrectionConnectionAsync(identity).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _core.Logger?.LogWarning(error,
                "Could not fence Redis client {ServerClientId} at {Endpoint} after an uncertain {Operation}; " +
                "the command may still execute. Ownership was already treated as lost.",
                identity.ServerClientId, identity.Endpoint, operation);
        }
    }

    internal async ValueTask<bool> ExecuteLockAsync(
        RespireKey key, RespireLockToken token, long? milliseconds, CancellationToken cancellationToken)
    {
        var execution = await StartLockExecutionAsync(key, token, milliseconds, false, cancellationToken)
            .ConfigureAwait(false);
        return await execution.Response.ConfigureAwait(false);
    }

    internal async ValueTask<TrackedLockExecution> StartLockExecutionAsync(
        RespireKey key, RespireLockToken token, long? milliseconds,
        bool requireIdentity, CancellationToken cancellationToken)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var cache = core.ClientCache;
        var mutationFence = cache is null ? default : cache.BeginUnknownMutation();
        var responseOwnsFence = false;
        try
        {
            var wireKey = Key(in key);
            int? slot = wireKey.TryGetClusterSlot(out var keySlot) ? keySlot : null;
            RespireConnection connection;
            if (requireIdentity && core.Cluster is { } cluster)
            {
                connection = await GetTrackedClusterConnectionAsync(cluster, slot, requireIdentity, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (requireIdentity)
            {
                var multiplexer = core.Sentinel is { } sentinel
                    ? (await sentinel.GetGenerationAsync(cancellationToken).ConfigureAwait(false)).Multiplexer
                    : core.Multiplexer;
                connection = await GetTrackedConnectionAsync(multiplexer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connection = await AcquireConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
            }

            var execution = new TrackedLockExecution(GetTrackedConnectionIdentity(connection, requireIdentity));
            var response = ExecuteRoutedLockAsync(
                execution, connection, wireKey, token.AsValue(), milliseconds, slot, requireIdentity, cancellationToken);
            execution.Response = mutationFence.IsRequired
                ? CompleteMutationAsync(response, cache!, mutationFence)
                : response;
            responseOwnsFence = mutationFence.IsRequired;
            return execution;
        }
        finally
        {
            if (mutationFence.IsRequired && !responseOwnsFence)
            {
                cache!.CompleteMutation(in mutationFence);
            }
        }
    }

    private async ValueTask<bool> ExecuteRoutedLockAsync(
        TrackedLockExecution execution, RespireConnection connection, RespireValue key,
        RespireValue token, long? milliseconds, int? slot, bool requireIdentity,
        CancellationToken cancellationToken)
    {
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            var sendAsking = false;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    execution.CommandMayBeOutstanding = true;
                    return await ExecuteCompatibleLockAsync(connection, key, token, milliseconds, sendAsking, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException error) when (
                    _core.Cluster is { } cluster && cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    // Retirement rejected the command before execution.
                    execution.CommandMayBeOutstanding = false;
                    cluster.RecordRejection(ref discovery, connection, error);
                    discoveryPending = true;
                    connection = requireIdentity
                        ? await GetTrackedReplacementConnectionAsync(
                            cluster, sendAsking ? connection : null, slot, true, cancellationToken, discovery).ConfigureAwait(false)
                        : await cluster.GetReplacementConnectionAsync(
                            sendAsking ? connection : null, slot, null, cancellationToken, discovery).ConfigureAwait(false);
                    discoveryPending = false;
                    execution.ConnectionIdentity = GetTrackedConnectionIdentity(connection, requireIdentity, sendAsking);
                }
                catch (RespireServerException error) when (
                    _core.Cluster is { } cluster && attempt < ClusterRouter.RedirectLimit && ClusterRouter.CanRecover(error, slot))
                {
                    // MOVED and ASK are definitive: this node did not run the command.
                    execution.CommandMayBeOutstanding = false;
                    cluster.RecordRejection(ref discovery, connection, error);
                    discoveryPending = true;
                    connection = requireIdentity
                        ? await GetTrackedRedirectConnectionAsync(
                                _core.Cluster, error, connection, true, cancellationToken, slot, discovery).ConfigureAwait(false)
                        : await _core.Cluster.GetRedirectConnectionAsync(error, connection, cancellationToken, slot, discovery)
                            .ConfigureAwait(false);
                    discoveryPending = false;
                    sendAsking = error.Code == RespireErrorCodes.Ask;
                    // Publish the identity before any write on the redirected connection can be sent.
                    execution.ConnectionIdentity = GetTrackedConnectionIdentity(connection, requireIdentity, sendAsking);
                }
                catch (Exception error) when (LockCommands.IsUnsubmitted(error))
                {
                    // The transport proved this attempt never reached Redis.
                    execution.CommandMayBeOutstanding = false;
                    throw;
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, slot);
            throw;
        }
        finally { discovery?.Finish(); }
    }

    private async ValueTask<bool> ExecuteCompatibleLockAsync(
        RespireConnection connection, RespireValue key, RespireValue token, long? milliseconds,
        bool sendAsking, CancellationToken cancellationToken)
    {
        var capabilities = LockConnectionCapabilities.GetValue(connection, static _ => new LockCapabilities());
        if (milliseconds is { } duration)
        {
            if (capabilities.CanTry(LockCapability.ConditionalSet))
            {
                try
                {
                    var reply = await SendOnConnectionAsync("SET", connection,
                            new ConditionalSetCommand(key, token, RespireValueCondition.EqualTo(token),
                                RespireExpiry.In(TimeSpan.FromMilliseconds(duration)), false),
                            cancellationToken, sendAsking: sendAsking)
                        .ConfigureAwait(false);
                    try { return !reply.IsNull; }
                    finally { reply.Dispose(); }
                }
                catch (RespireServerException error) when (error.Message == "ERR syntax error")
                {
                    // All other SET arguments were validated before sending. This reply means
                    // IFEQ was rejected before execution, so trying the Lua equivalent is safe.
                    capabilities.Reject(LockCapability.ConditionalSet);
                }
            }
        }
        else
        {
            if (capabilities.CanTry(LockCapability.Delex))
            {
                try
                {
                    return await SendLockIntegerAsync("DELEX", connection,
                            new Cmd3(RespireCommands.String.DELEX.Verb, key, "IFEQ", token), sendAsking, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RespireServerException error) when (IsUnknownLockCommand(error, "DELEX"))
                {
                    capabilities.Reject(LockCapability.Delex);
                }
            }

            if (capabilities.CanTry(LockCapability.Delifeq))
            {
                try
                {
                    return await SendLockIntegerAsync("DELIFEQ", connection,
                            new Cmd2(RespireCommands.String.DELIFEQ.Verb, key, token), sendAsking, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RespireServerException error) when (IsUnknownLockCommand(error, "DELIFEQ"))
                {
                    capabilities.Reject(LockCapability.Delifeq);
                }
            }
        }

        var script = milliseconds.HasValue ? LockCommands.ExtendScript : LockCommands.ReleaseScript;
        RespireValue[] args = milliseconds is { } expiry ? [key, token, expiry] : [key, token];
        try
        {
            return await SendLockIntegerAsync(script.EvalShaOperation, connection,
                    new Cmd2N(script.EvalShaVerb, script.Sha1, 1, args), sendAsking, cancellationToken, script.Sha1)
                .ConfigureAwait(false);
        }
        catch (RespireServerException error) when (error.Code == RespireErrorCodes.NoScript)
        {
            return await SendLockIntegerAsync(script.EvalOperation, connection,
                    new Cmd2N(script.EvalVerb, script.Source, 1, args), sendAsking, cancellationToken, script.Sha1)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> SendLockIntegerAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, bool sendAsking,
        CancellationToken cancellationToken, string? storedProcedureName = null)
        where TCommand : struct, IRespCommand
    {
        var reply = await SendOnConnectionAsync(operation, connection, command, cancellationToken,
                storedProcedureName, sendAsking)
            .ConfigureAwait(false);
        try { return reply.AsInteger() >= 1; }
        finally { reply.Dispose(); }
    }

    private static bool IsUnknownLockCommand(RespireServerException error, string command)
        => error.Code == "ERR"
            && error.Message.StartsWith($"ERR unknown command '{command}',", StringComparison.OrdinalIgnoreCase);
}
