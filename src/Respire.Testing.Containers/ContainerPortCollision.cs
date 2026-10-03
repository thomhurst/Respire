using System.Net;
using System.Globalization;
using System.Text.Json;
using Docker.DotNet;

namespace Respire.Testing.Containers;

internal static class ContainerPortCollision
{
    internal static bool IsMatch(Exception error, int[] selectedPorts)
    {
        // Docker has no typed port-conflict subtype. Require its structured API response,
        // and a known address-in-use bind failure. Most formats also name the selected port.
        if (error is not DockerApiException { StatusCode: HttpStatusCode.InternalServerError, ResponseBody: { } body })
            return false;
        string? message;
        try
        {
            using var response = JsonDocument.Parse(body);
            if (response.RootElement.ValueKind != JsonValueKind.Object
                || !response.RootElement.TryGetProperty("message", out var value)
                || value.ValueKind != JsonValueKind.String) return false;
            message = value.GetString();
        }
        catch (JsonException) { return false; }
        if (message is null) return false;
        // Recent Linux engines omit the host address in this libnetwork TCP bind error.
        // The fixture calls this only for StartAsync failures with explicit port mappings;
        // retain the complete networking prefix and TCP bind suffix, not a generic match.
        if (selectedPorts.Length != 0
            && message.StartsWith("failed to set up container networking: driver failed programming external connectivity on endpoint ", StringComparison.Ordinal)
            && message.EndsWith("): failed to listen on TCP socket: address already in use", StringComparison.Ordinal))
            return true;
        foreach (var port in selectedPorts)
        {
            var address = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            // Moby's port allocator and Linux bindTCPOrUDP error formats.
            if (message.EndsWith($"Bind for {address} failed: port is already allocated", StringComparison.Ordinal))
                return true;
            if (message.Contains($"failed to bind host port for {address}:", StringComparison.Ordinal)
                && message.EndsWith("/tcp: address already in use", StringComparison.Ordinal))
                return true;
            // Docker Desktop forwards the platform's TCP bind error. Permission-denied,
            // reserved-port and unknown messages deliberately remain non-retryable.
            if (message.StartsWith("Ports are not available:", StringComparison.OrdinalIgnoreCase)
                && (message.Contains($"listen tcp {address}: bind: ", StringComparison.Ordinal)
                    || message.Contains($"listen tcp4 {address}: bind: ", StringComparison.Ordinal))
                && (message.EndsWith(": bind: address already in use", StringComparison.Ordinal)
                    || message.EndsWith(": bind: Only one usage of each socket address (protocol/network address/port) is normally permitted.", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }
}
