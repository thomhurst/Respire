using System.Net;
using System.Globalization;
using System.Text.Json;
using Docker.DotNet;

namespace Respire.Testing.Containers;

internal static class ContainerPortCollision
{
    /// <summary>Recognizes a structured Docker bind collision for explicitly selected host ports.</summary>
    internal static bool IsMatch(Exception error, int[] selectedPorts)
    {
        var message = DockerMessage(error);
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

    /// <summary>Recognizes the standalone TCP bind collision with an empty random host port and a selected container port.</summary>
    internal static bool IsStandaloneBindCollision(Exception error, int[] containerPorts)
        => DockerMessage(error) is { } message && IsRandomHostPortCollision(message, containerPorts);

    private static string? DockerMessage(Exception error)
    {
        // Docker has no typed port-conflict subtype. Both matchers require the structured HTTP 500 response.
        if (error is not DockerApiException { StatusCode: HttpStatusCode.InternalServerError, ResponseBody: { } body })
            return null;
        try
        {
            using var response = JsonDocument.Parse(body);
            return response.RootElement.ValueKind == JsonValueKind.Object
                && response.RootElement.TryGetProperty("message", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsRandomHostPortCollision(string message, int[] containerPorts)
    {
        const string prefix = "failed to set up container networking: driver failed programming external connectivity on endpoint ";
        const string binding = "): failed to bind host port for 127.0.0.1::";
        const string suffix = "/tcp: address already in use";
        if (!message.StartsWith(prefix, StringComparison.Ordinal)
            || !message.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var bindingIndex = message.IndexOf(binding, prefix.Length, StringComparison.Ordinal);
        if (bindingIndex < 0) return false;
        var destination = message.AsSpan(bindingIndex + binding.Length);
        destination = destination[..^suffix.Length];
        var separator = destination.LastIndexOf(':');
        // Docker leaves the requested random host port empty but includes the container address/port.
        return separator > 0
            && IPAddress.TryParse(destination[..separator], out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && int.TryParse(destination[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && Array.IndexOf(containerPorts, port) >= 0;
    }
}
