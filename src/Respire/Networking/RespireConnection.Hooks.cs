using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    public static Task<RespireConnection> ConnectAsync(
        string host, int port, RespireConnectionOptions? options = null, ILogger? logger = null,
        CancellationToken cancellationToken = default, bool armHandshakeDeadline = true)
    {
        options ??= RespireConnectionOptions.Default;
        // Keep the default path free of hook contexts, delegates and async wrappers.
        return options.ConnectionHook is { } hook
            ? ConnectWithHookAsync(host, port, options, logger, cancellationToken, armHandshakeDeadline, hook)
            : ConnectCoreAsync(host, port, options, logger, cancellationToken, armHandshakeDeadline);
    }

    private static async Task<RespireConnection> ConnectWithHookAsync(
        string host, int port, RespireConnectionOptions options, ILogger? logger,
        CancellationToken cancellationToken, bool armHandshakeDeadline, RespireConnectionHook hook)
    {
        var context = new RespireConnectionContext(new(host, port), options.UseTls);
        var started = Stopwatch.GetTimestamp();
        RespireConnection? connection = null;
        var completing = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await hook.OnConnectingAsync(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            connection = await ConnectCoreAsync(host, port, options, logger, cancellationToken, armHandshakeDeadline)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            completing = true;
            await hook.OnCompletedAsync(context, RespireConnectionOutcome.Succeeded,
                Stopwatch.GetElapsedTime(started), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch (Exception error)
        {
            if (connection is not null)
            {
                connection.RequestMetricCloseReason(Internal.ConnectionTelemetry.CloseReason.Application);
                connection.Abort(error, observeErrors: false);
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the hook or cancellation failure. */ }
            }
            // A completion callback is never invoked twice, even when it throws.
            if (!completing)
            {
                var outcome = Internal.ConnectionTelemetry.IsCallerCancellation(error, cancellationToken)
                    ? RespireConnectionOutcome.Canceled : RespireConnectionOutcome.Failed;
                try
                {
                    await hook.OnCompletedAsync(context, outcome, Stopwatch.GetElapsedTime(started), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch { /* An audit failure must not hide the original connection failure. */ }
            }
            throw;
        }
    }
}
