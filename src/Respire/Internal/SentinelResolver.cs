using System.Globalization;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;

namespace Respire.Internal;

internal static class SentinelResolver
{
    public static async ValueTask<TResult> ResolveAndConnectPrimaryAsync<TResult>(
        RespireOptions options,
        Func<RespireOptions, CancellationToken, ValueTask<TResult>> connectPrimaryAsync,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            return await connectPrimaryAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (options.Cluster)
        {
            throw new ArgumentException(
                "Redis Sentinel discovery and Redis Cluster routing cannot both be enabled.",
                nameof(options));
        }

        var sentinelEndpoints = options.Endpoints.Count == 0
            ? [new RespireEndpoint("localhost", 26379)]
            : options.Endpoints.ToArray();
        var sentinelOptions = CreateSentinelConnectionOptions(options);
        var logger = options.CreateLogger("Respire.Sentinel");
        Exception? lastError = null;

        foreach (var endpoint in sentinelEndpoints)
        {
            using var discoveryTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            discoveryTimeoutSource.CancelAfter(options.CommandTimeout ?? options.ConnectTimeout);
            try
            {
                var primary = await QueryPrimaryAsync(
                        endpoint,
                        options.ServiceName!,
                        sentinelOptions,
                        logger,
                        discoveryTimeoutSource.Token)
                    .ConfigureAwait(false);
                var primaryOptions = options with
                {
                    Endpoints = new List<RespireEndpoint> { primary },
                    ServiceName = null,
                };
                using var connectTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeoutSource.CancelAfter(options.ConnectTimeout);
                return await connectPrimaryAsync(primaryOptions, connectTimeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger?.LogWarning(
                    ex,
                    "Redis Sentinel discovery or primary connection failed through {Host}:{Port}",
                    endpoint.Host,
                    endpoint.Port);
            }
        }

        var message =
            $"Unable to discover and connect to Redis Sentinel service '{options.ServiceName}' " +
            $"from {sentinelEndpoints.Length} endpoint(s).";
        throw lastError is null
            ? new RespireConnectionException(message)
            : new RespireConnectionException(message, lastError);
    }

    internal static RespireConnectionOptions CreateSentinelConnectionOptions(RespireOptions options)
    {
        var authenticationDisabled = options.SentinelPassword is { Length: 0 };
        return options.ToConnectionOptions() with
        {
            Username = authenticationDisabled ? null : options.SentinelUsername ?? options.Username,
            Password = authenticationDisabled ? null : options.SentinelPassword ?? options.Password,
            ClientName = null,
            Database = 0,
            UseResp3 = false,
            UseTls = options.SentinelUseTls ?? options.UseTls,
            TlsOptions = options.SentinelTlsOptions ?? options.TlsOptions,
            PushHandler = null,
        };
    }

    private static async ValueTask<RespireEndpoint> QueryPrimaryAsync(
        RespireEndpoint sentinel,
        string serviceName,
        RespireConnectionOptions options,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        await using var connection = await RespireConnection.ConnectAsync(
                sentinel.Host,
                sentinel.Port,
                options,
                logger,
                cancellationToken)
            .ConfigureAwait(false);
        var reply = await connection.SendAsync(
                new Cmd1(Verbs.SentinelGetMasterAddressByName, serviceName),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (reply.IsError)
            {
                throw new RespireServerException(reply.GetErrorMessage(), "SENTINEL GET-MASTER-ADDR-BY-NAME");
            }

            if (reply.IsNull)
            {
                throw new RespireConnectionException(
                    $"Redis Sentinel service '{serviceName}' was not found on {sentinel}.");
            }

            var parts = reply.AsArray();
            if (parts.Length < 2)
            {
                throw new RespireProtocolException(
                    $"Redis Sentinel returned {parts.Length} fields for service '{serviceName}', expected host and port.");
            }

            var host = parts[0].AsString();
            var portText = parts[1].AsString();
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
            {
                throw new RespireProtocolException(
                    $"Redis Sentinel returned invalid port '{portText}' for service '{serviceName}'.");
            }

            return new RespireEndpoint(host, port);
        }
        finally
        {
            reply.Dispose();
        }
    }
}
