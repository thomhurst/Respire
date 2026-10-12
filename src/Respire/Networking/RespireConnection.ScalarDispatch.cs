using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    // SendNativeConvertedAsync for the stateless scalar shapes: the admitted source is
    // specialized on the converter struct. Capacity waits and retirement recovery keep the
    // dispatch owner and the general converted route, as the converted path does.
    internal ValueTask<TResult> SendNativeScalarAsync<TCommand, TConverter, TResult>(in TCommand command,
        CancellationToken cancellationToken, string operation)
        where TCommand : struct, IRespCommand
        where TConverter : struct, IScalarResponseConverter<TResult>
    {
        var started = RespireTelemetry.CaptureOperationStart(operation);
        var source = ScalarPendingResponseSource<TConverter, TResult>.Rent(operation,
            duration: new RespireTelemetry.DurationObservation(this, started));
        var response = source.Task;
        if (TryAdmitNative(in command, source, cancellationToken, started.Timestamp != 0, out var destination, out var deadline, out var retirement)) return response;
        return DispatchResponseSource<TResult>.Run(
            (Connection: destination, Command: command, Token: cancellationToken, Operation: operation, Deadline: deadline,
                Retirement: retirement, Started: started, FailedEndpoint: new RespireEndpoint(Host, Port)),
            static (state, observation) =>
            {
                if (state.Retirement is not null) observation.Handled(state.Retirement, state.FailedEndpoint);
                return state.Connection.SendConvertedAsync(state.Command, default(TConverter),
                    ScalarResponseConverter<TConverter, TResult>.Delegate, false,
                    state.Token, state.Operation, commandDeadline: state.Deadline, observation: observation, durationStarted: state.Started);
            });
    }
}
