using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private ValueTask<RespValue> ObserveScriptingReply<TCommand>(ValueTask<RespValue> reply, in TCommand command, string? commandName,
        CancellationToken cancellationToken, CommandDeadline deadline)
        where TCommand : struct, IRespCommand
        => ScriptingEngineInfo.IsScriptingCommand(commandName)
            ? ObserveScriptingReplyAsync(reply, commandName!, ScriptingEngineInfo.ExpectedEngine(in command, commandName!),
                cancellationToken, deadline) : reply;

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> ObserveScriptingReplyAsync(ValueTask<RespValue> reply, string commandName,
        string? expectedEngine, CancellationToken cancellationToken, CommandDeadline deadline)
    {
        RespValue value;
        try { value = await reply.ConfigureAwait(false); }
        catch (RespireServerException error)
        {
            var classified = await ClassifyScriptingErrorAsync(error, expectedEngine, cancellationToken, deadline).ConfigureAwait(false);
            if (ReferenceEquals(classified, error)) throw;
            throw classified;
        }
        if (!value.IsError) return value;
        var serverError = ResponseReader.ServerError(in value, commandName);
        Exception classifiedError;
        try
        {
            classifiedError = await ClassifyScriptingErrorAsync(serverError, expectedEngine, cancellationToken, deadline).ConfigureAwait(false);
        }
        catch { value.Dispose(); throw; }
        if (ReferenceEquals(classifiedError, serverError)) return value;
        value.Dispose();
        throw classifiedError;
    }

    internal async ValueTask<Exception> ClassifyScriptingErrorAsync(RespireServerException error, string? expectedEngine,
        CancellationToken cancellationToken, CommandDeadline deadline = default)
    {
        var engine = ScriptingEngineInfo.MissingEngine(error, expectedEngine);
        if (engine is null) return error;
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = Math.Min(1000L, deadline.RemainingMilliseconds);
        if (remaining <= 0) return error;
        // CommandTimeout=null disables the connection's sweep, so the optional diagnostic
        // needs its own timer as well as an admission/response deadline.
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        try
        {
            // Evidence belongs to this socket and this failed command only. Never route this
            // diagnostic to a replacement connection or retain a stale module inventory.
            using var reply = await SendCoreAsync(new Cmd1(Verbs.Info, "scriptingengines"),
                discardRepliesBefore: 0, throwOnError: false, probeCancellation.Token,
                commandName: "INFO scriptingengines", commandDeadline: CommandDeadline.After(remaining),
                pinToConnection: true).ConfigureAwait(false);
            if (reply.Type is RespDataType.BulkString or RespDataType.SimpleString or RespDataType.VerbatimString
                && ScriptingEngineInfo.ConfirmsAbsence(reply.AsString(), engine))
                return new RespireScriptingEngineUnavailableException(engine, new RespireEndpoint(Host, Port), error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (probeCancellation.IsCancellationRequested) { }
        catch (RespireException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        return error;
    }
}
