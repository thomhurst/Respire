using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Respire.Testing.Containers;

internal static class PublishedRedisIdentity
{
    private const int MaximumHeaderLength = 256;
    private const int MaximumInfoLength = 16 * 1024;

    // Probe on one connection with a read-only command. The listener can belong to another service.
    // A null run ID identifies an HTTP response. Incomplete or invalid replies fail verification.
    internal static async Task<string?> ReadRunIdAsync(RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync("*2\r\n$4\r\nINFO\r\n$6\r\nserver\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        using var response = new BufferedStream(stream);
        string header = await ReadHeaderAsync(response, cancellationToken).ConfigureAwait(false);

        if (header.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return null;
        }

        if (!header.StartsWith('$')
            || !int.TryParse(header.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
            || length is <= 0 or > MaximumInfoLength)
        {
            throw new IOException($"Unexpected INFO server reply header: {header}");
        }

        var payload = new byte[length + 2];
        await response.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        if (!payload.AsSpan(length).SequenceEqual("\r\n"u8))
        {
            throw new IOException("INFO server reply did not end with CRLF.");
        }

        return ParseRunId(Encoding.UTF8.GetString(payload, 0, length));
    }

    internal static string ParseRunId(string info)
    {
        const string prefix = "run_id:";

        foreach (string line in info.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                string runId = line[prefix.Length..].Trim();

                if (runId.Length != 0)
                {
                    return runId;
                }
            }
        }

        throw new IOException("INFO server did not include a run_id.");
    }

    private static async Task<string> ReadHeaderAsync(Stream response, CancellationToken cancellationToken)
    {
        var header = new byte[MaximumHeaderLength];

        for (int index = 0; index < header.Length; index++)
        {
            await response.ReadExactlyAsync(header.AsMemory(index, 1), cancellationToken).ConfigureAwait(false);

            if (index > 0 && header[index - 1] == '\r' && header[index] == '\n')
            {
                return Encoding.ASCII.GetString(header, 0, index - 1);
            }
        }

        throw new IOException($"INFO server reply header exceeded {MaximumHeaderLength} bytes.");
    }
}
