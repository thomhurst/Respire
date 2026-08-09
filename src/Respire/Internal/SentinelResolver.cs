using System.Globalization;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;

namespace Respire.Internal;

internal static class SentinelResolver
{
    public static async ValueTask<RespireOptions> ResolvePrimaryAsync(
        RespireOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            return options;
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
        var sentinelOptions = options.ToConnectionOptions() with
        {
            Username = options.SentinelUsername ?? options.Username,
            Password = options.SentinelPassword ?? options.Password,
            Database = 0,
            PushHandler = null,
        };
        var logger = options.CreateLogger("Respire.Sentinel");
        Exception? lastError = null;

        foreach (var endpoint in sentinelEndpoints)
        {
            try
            {
                var primary = await QueryPrimaryAsync(
                        endpoint,
                        options.ServiceName!,
                        sentinelOptions,
                        logger,
                        cancellationToken)
                    .ConfigureAwait(false);
                return options with
                {
                    Endpoints = new List<RespireEndpoint> { primary },
                    ServiceName = null,
                };
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
                    "Redis Sentinel discovery failed through {Host}:{Port}",
                    endpoint.Host,
                    endpoint.Port);
            }
        }

        var message =
            $"Unable to discover Redis Sentinel service '{options.ServiceName}' from {sentinelEndpoints.Length} endpoint(s).";
        throw lastError is null
            ? new RespireConnectionException(message)
            : new RespireConnectionException(message, lastError);
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
                throw new RespireServerException(reply.GetErrorMessage());
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
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
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
