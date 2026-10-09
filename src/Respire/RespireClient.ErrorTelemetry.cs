using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // Correction callers retain the error lease through the reply and decide whether a
    // failure is propagated or handled. Preserve normal script retries and cache fences.
    internal ValueTask<RespireResult> ExecuteScriptBorrowedAsync(
        RespireScript script, RespireKey[] keys, RespireValue[] args,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (_core.Disposed) ThrowIfDisposedForCommand(observeErrors: false);
        var tail = BuildScriptTail(keys, args);
        var cache = _core.ClientCache;
        var fence = cache is null || script.IsCacheReadOnly ? default : cache.BeginUnknownMutation();
        var response = ExecuteScriptCoreAsync(script, tail, cancellationToken, fence, observation);
        return fence.IsRequired ? CompleteMutationAsync(response, cache!, fence) : response;
    }

    // A supplied observation is transferred: the facet started it before argument preflight.
    internal ValueTask<RespireResult> ExecuteScriptAsync(
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var core = _core;
        if (observation.IsEmpty) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            if (core.Disposed) ThrowIfDisposedForCommand(observeErrors: false);
            var cache = core.ClientCache;
            var mutationFence = cache is null || script.IsCacheReadOnly ? default : cache.BeginUnknownMutation();
            var response = ExecuteScriptCoreAsync(script, tail, cancellationToken, mutationFence, observation);
            return RespireTelemetry.ObserveFinalError(
                mutationFence.IsRequired ? CompleteMutationAsync(response, cache!, mutationFence) : response, observation);
        }
        catch (Exception error)
        {
            observation.Final(error);
            observation.Dispose();
            throw;
        }
    }

    // Setup owns a new observation until the execution is returned. A supplied observation
    // remains borrowed through setup, routing, the raw response and the caller's cleanup.
    // Direct readers of owned executions or correction boundaries return their observation.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<TrackedScriptExecution> StartTrackedScriptExecutionAsync(
        RespireScript script, RespireKey[] keys, RespireValue[] args, CancellationToken cancellationToken,
        bool requireReliableCorrectionOrdering = false, bool captureSendTimestampOnly = false,
        Action<long>? onSerialized = null, Action? onCommandNotApplied = null,
        RespireTelemetry.ErrorObservation errorObservation = default)
    {
        var ownsObservation = errorObservation.IsEmpty;
        var observation = ownsObservation ? RespireTelemetry.ErrorObservation.Rent(force: true) : errorObservation;
        try
        {
            return await StartTrackedScriptExecutionCoreAsync(script, keys, args, cancellationToken,
                requireReliableCorrectionOrdering, captureSendTimestampOnly, onSerialized, onCommandNotApplied,
                observation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (ownsObservation)
            {
                observation.Final(error);
                observation.Dispose();
            }
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    // A supplied owner is borrowed on failure, so its caller can classify a fallback as handled.
    // On success the returned execution owns either lease.
    internal async ValueTask<TrackedLockExecution> StartLockExecutionAsync(
        RespireKey key, RespireLockToken token, long? milliseconds, bool requireIdentity,
        bool allowUnfencedFallback, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation callerObservation = default)
    {
        var ownsObservation = callerObservation.IsEmpty;
        var observation = ownsObservation ? RespireTelemetry.ErrorObservation.Rent(force: true) : callerObservation;
        try
        {
            return await StartLockExecutionCoreAsync(key, token, milliseconds, requireIdentity,
                allowUnfencedFallback, cancellationToken, observation).ConfigureAwait(false);
        }
        catch (Exception error) when (ownsObservation)
        {
            observation.Final(error);
            observation.Dispose();
            throw;
        }
    }

    private ValueTask<RespValue> SendAsync<TCommand>(
        string operation,
        in TCommand command,
        CancellationToken cancellationToken,
        RespireCommandFlags flags,
        bool allowReadFrom = true,
        ReadAffinity? cursorAffinity = null, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        if (!observation.IsEmpty)
            return SendCoreAsync(operation, command, cancellationToken, flags, allowReadFrom,
                cursorAffinity, observation, observeErrors: false);
        if (flags == RespireCommandFlags.None && cursorAffinity is null && !RespireTelemetry.IsOperationEnabled(operation)
            && TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeCheckedAsync(in command, cancellationToken, operation);
        return DispatchResponseSource<RespValue>.Run(
            (Client: this, Operation: operation, Command: command, Token: cancellationToken,
                Flags: flags, Read: allowReadFrom, Affinity: cursorAffinity),
            static (state, observation) => state.Client.SendCoreAsync(state.Operation, state.Command,
                state.Token, state.Flags, state.Read, state.Affinity, observation, observeErrors: false));
    }

    // A correction scope supplies the lease and reports only after recovery and cleanup.
    internal ValueTask<RespValue> SendForCorrectionAsync<TCommand>(
        string operation, TCommand command, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        => SendCoreAsync(operation, command, cancellationToken, RespireCommandFlags.None,
            allowReadFrom: true, cursorAffinity: null, observation: observation, observeErrors: false);

    /// <summary>Sends a streaming GET through the current standalone or Cluster route.</summary>
    internal ValueTask<Stream?> SendBulkStreamAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        // Keep attempts even if metrics are enabled while this streaming operation is pending.
        if (observation.IsEmpty)
            return DispatchResponseSource<Stream?>.Run(
                (Client: this, Operation: operation, Command: command, Token: cancellationToken),
                static (state, owner) => state.Client.SendBulkStreamAsync(state.Operation, state.Command, state.Token, owner));
        try
        {
            return RespireTelemetry.ObserveStreamError(
                SendBulkStreamCoreAsync(operation, command, cancellationToken, observation), observation);
        }
        catch (Exception error)
        {
            observation.Final(error);
            observation.Dispose();
            throw;
        }
    }

    // Physical handles cannot follow transport retirement to a replacement socket. Keep the
    // same telemetry/error path, including atomic ASKING prefixes on checked destinations.
    internal ValueTask<RespValue> SendOnPinnedConnectionAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default, bool sendAsking = false)
        where TCommand : struct, IRespCommand
        => SendOnConnectionObservedAsync(operation, connection, command, cancellationToken,
            pinToConnection: true, observation: observation, sendAsking: sendAsking);

    // Use only for direct public routes. Routing, batch, hedge and correction scopes own their
    // final boundary and must keep using the unobserved transport entry point below.
    internal ValueTask<RespValue> SendOnConnectionObservedAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default, bool sendAsking = false)
        where TCommand : struct, IRespCommand
    {
        if (observation.IsEmpty)
            return DispatchResponseSource<RespValue>.Run(
                (Client: this, Operation: operation, Connection: connection, Command: command,
                    Token: cancellationToken, Pinned: pinToConnection, Asking: sendAsking),
                static (state, owner) => state.Client.SendOnConnectionObservedAsync(state.Operation,
                    state.Connection, state.Command, state.Token, state.Pinned, owner, state.Asking));
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return RespireTelemetry.IsOperationEnabled(operation)
            ? SendOnConnectionInstrumentedAsync(operation, connection, command, cancellationToken,
                storedProcedureName: null, sendAsking: sendAsking, commandDeadline: default,
                allowStreamingConnectionReroute: !pinToConnection, pinToConnection: pinToConnection,
                observation: observation)
            : SendOnConnectionCoreAsync(operation, connection, command, cancellationToken, sendAsking,
                allowStreamingConnectionReroute: !pinToConnection, pinToConnection: pinToConnection,
                observation: observation);
    }

    private ValueTask<TResult> ConvertCachedResponseAsync<TCommand, TState, TResult>(
        string operation, TCommand command, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, TResult> converter, bool observeErrors,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => observeErrors
            ? ConvertResponseAsync(operation, command, cancellationToken, state, converter)
            : ConvertUnobservedResponseAsync(operation, command, cancellationToken, state, converter, observation);

    // Cache fallbacks, continuous reads and failover probes borrow their logical owner's error boundary,
    // including reply conversion. The caller decides whether a failure is final or handled.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<TResult> ConvertUnobservedResponseAsync<TCommand, TState, TResult>(
        string operation, TCommand command, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, TResult> converter, RespireTelemetry.ErrorObservation observation,
        bool blocking = false)
        where TCommand : struct, IRespCommand
    {
        using var response = await (blocking
            ? SendBlockingAsync(operation, command, cancellationToken, observeErrors: false, observation: observation)
            : SendCoreAsync(operation, command, cancellationToken, RespireCommandFlags.None,
                allowReadFrom: true, cursorAffinity: null, observation: observation, observeErrors: false)).ConfigureAwait(false);
        return converter(state, in response);
    }

    private ValueTask<TResult> ConvertObservedResponseAsync<TCommand, TState, TResult>(
        string operation, TCommand command, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, TResult> converter, bool transferOwnership,
        ReadAffinity? cursorAffinity = null, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        if (observation.IsEmpty)
            return DispatchResponseSource<TResult>.Run(
                (Client: this, Operation: operation, Command: command, Token: cancellationToken,
                    State: state, Converter: converter, Transfer: transferOwnership, Affinity: cursorAffinity),
                static (state, owner) => state.Client.ConvertObservedResponseAsync(state.Operation, state.Command,
                    state.Token, state.State, state.Converter, state.Transfer, state.Affinity, owner));
        ClientSideCacheCoordinator.MutationFence mutationFence = default;
        ValueTask<RespValue> response;
        try
        {
            response = SendCoreAsync(operation, command, cancellationToken, RespireCommandFlags.None,
                allowReadFrom: true, cursorAffinity: cursorAffinity, out mutationFence,
                observation: observation, observeErrors: false, deferMutationCompletion: true);
            if (mutationFence.IsRequired)
            {
                // Conversion decides the logical outcome; a successful raw reply alone
                // must not retire the caller's fence or suppress failure reinvalidation.
                var converted = PooledResponseSource<TState, TResult>.Create(response, state, converter,
                    transferOwnership, observeErrors: false);
                return CompleteObservedMutationAsync(converted, _core.ClientCache!, mutationFence, observation);
            }
        }
        catch (Exception error)
        {
            if (mutationFence.IsRequired)
            {
                try { _core.ClientCache!.CompleteMutation(in mutationFence); }
                catch (Exception) { /* Preserve the dispatch or converter failure. */ }
            }
            observation.Final(error);
            observation.Dispose();
            throw;
        }
        // Create owns the observation, including synchronous conversion failures.
        return PooledResponseSource<TState, TResult>.Create(response, state, converter, transferOwnership, observation);
    }

    internal ValueTask<TResult> ConvertCursorPageAsync<TCommand, TState, TResult>(
        string operation, TCommand command, ReadAffinity affinity, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, TResult> converter)
        where TCommand : struct, IRespCommand
        => ConvertObservedResponseAsync(operation, command, cancellationToken, state, converter, false,
            _readFrom != RespireReadFrom.Primary && command.ReadKind == ReadCommandKind.CursorRead ? affinity : null);

    internal ValueTask<TResult> ConvertBlockingResponseAsync<TCommand, TState, TResult>(
        string operation, TCommand command, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, TResult> converter, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        if (observation.IsEmpty)
            return DispatchResponseSource<TResult>.Run(
                (Client: this, Operation: operation, Command: command, Token: cancellationToken, State: state, Converter: converter),
                static (state, owner) => state.Client.ConvertBlockingResponseAsync(
                    state.Operation, state.Command, state.Token, state.State, state.Converter, owner));
        // SendBlockingAsync captures preflight errors in its ValueTask. The converter owns
        // the lease through dedicated connection cleanup and final reply publication.
        var response = SendBlockingAsync(operation, command, cancellationToken, observeErrors: false, observation: observation);
        return PooledResponseSource<TState, TResult>.Create(response, state, converter, observation: observation);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<TResult> ExecuteScriptConvertedAsync<TResult>(
        RespireScript script, RespireValue[] tail, CancellationToken cancellationToken,
        Func<RespireResult, TResult> convert, RespireTelemetry.ErrorObservation transferred = default)
    {
        // A transferred observation already covers the facet's argument preflight.
        using var observation = transferred.IsEmpty ? RespireTelemetry.ErrorObservation.Rent(force: true) : transferred;
        try
        {
            if (_core.Disposed) ThrowIfDisposedForCommand(observeErrors: false);
            var cache = _core.ClientCache;
            var fence = cache is null || script.IsCacheReadOnly ? default : cache.BeginUnknownMutation();
            var response = ExecuteScriptCoreAsync(script, tail, cancellationToken, fence, observation);
            using var result = await (fence.IsRequired ? CompleteMutationAsync(response, cache!, fence) : response)
                .ConfigureAwait(false);
            return convert(result);
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
    }

    internal ValueTask<TResult> ConvertOnConnectionAsync<TCommand, TState, TResult>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, TResult> converter, bool pinToConnection = true,
        RespireTelemetry.ErrorObservation observation = default, bool admit = false)
        where TCommand : struct, IRespCommand
    {
        if (observation.IsEmpty)
            return DispatchResponseSource<TResult>.Run(
                (Client: this, Operation: operation, Connection: connection, Command: command, Token: cancellationToken,
                    State: state, Converter: converter, Pinned: pinToConnection, Admit: admit),
                static (state, owner) => state.Client.ConvertOnConnectionAsync(state.Operation, state.Connection,
                    state.Command, state.Token, state.State, state.Converter, state.Pinned, owner, state.Admit));
        ValueTask<RespValue> response;
        try
        {
            // Admitted routes are pinned and complete any client-cache mutation fence before conversion.
            response = admit
                ? SendAdmittedOnPinnedConnectionAsync(operation, connection, command, cancellationToken, observation)
                : SendOnConnectionObservedAsync(operation, connection, command, cancellationToken,
                    pinToConnection, observation);
        }
        catch (Exception error)
        {
            observation.Final(error);
            observation.Dispose();
            throw;
        }
        return PooledResponseSource<TState, TResult>.Create(response, state, converter, observation: observation);
    }

    private void ThrowIfDisposedForCommand(bool observeErrors = true)
    {
        if (!_core.Disposed) return;
        try { ObjectDisposedException.ThrowIf(true, this); }
        catch (ObjectDisposedException error)
        {
            if (observeErrors) RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }
    }

    private static void RecordRetryError(Exception error, int retryAttempts,
        ClusterScriptTelemetry? scriptTelemetry, Action<Exception>? onRetry,
        RespireTelemetry.ErrorObservation observation)
    {
        if (!observation.IsEmpty) observation.Handled(error);
        else if (scriptTelemetry is not null) scriptTelemetry.RecordRetry(error);
        else if (onRetry is not null) onRetry(error);
        else RespireTelemetry.RecordError(error, internallyHandled: true, retryAttempts);
    }

}
