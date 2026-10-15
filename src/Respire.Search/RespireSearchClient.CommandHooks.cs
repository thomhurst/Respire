using Respire.Internal;

namespace Respire.Search;

public sealed partial class RespireSearchClient
{
    private ValueTask<TResult> ExecuteAsync<TResult>(RespireCommand command, RespireValue[] arguments,
        CancellationToken token, Func<RespireResult, TResult> convert)
        => ExecuteAsync(command, arguments, token, async (response, _) =>
        {
            using var result = await response.ConfigureAwait(false);
            return convert(result);
        });

    private ValueTask<TResult> ExecuteAsync<TResult>(RespireCommand command, RespireValue[] arguments,
        CancellationToken token,
        Func<ValueTask<RespireResult>, RespireTelemetry.ErrorObservation, ValueTask<TResult>> convert)
        => _client is RespireClient concrete
            ? concrete.ExecuteModuleAsync(command, arguments, token, convert)
            : token.IsCancellationRequested
                ? ValueTask.FromCanceled<TResult>(token)
                : convert(ExecuteBorrowedAsync(command, arguments, token, default), default);

    private async ValueTask ExecuteAsync(RespireCommand command, RespireValue[] arguments, CancellationToken token)
        => _ = await ExecuteAsync(command, arguments, token, static _ => true).ConfigureAwait(false);

    private ValueTask<RespireResult> ExecuteBorrowedAsync(RespireCommand command, RespireValue[] arguments,
        CancellationToken token, RespireTelemetry.ErrorObservation owner)
    {
        try
        {
            return _client is RespireClient concrete
                ? concrete.ExecuteModuleBorrowedAsync(command, arguments, token, owner)
                : _client.ExecuteLeaseAsync(command, arguments, cancellationToken: token);
        }
        catch (Exception error)
        {
            // A decorator may throw synchronously. Let the typed owner classify it just
            // like an asynchronous reply failure, including hybrid capability recovery.
            return ValueTask.FromException<RespireResult>(error);
        }
    }
}
