using System.Globalization;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Containers;
using Respire.Testing.Containers;

namespace Respire.Testing;

/// <summary>
/// Starts a Testcontainers container that publishes Docker-assigned (random) host ports, retrying
/// confirmed host-port collisions on a fresh container.
/// </summary>
/// <remarks>
/// Docker picks a random host port and then listens on it; on a busy host another socket can take the
/// port in between, and the start fails with "failed to listen on TCP socket: address already in use".
/// A container whose start failed cannot be started again, so every attempt builds a new one from
/// <c>createContainer</c> and removes the failed one. Redis fixtures also verify the published
/// endpoint's server identity after Docker starts. Containers that publish fixed host ports need
/// fresh ports per attempt and use their own retries instead (see <c>RespireContainerFixture</c>).
/// </remarks>
internal static class ContainerStartRetry
{
    internal const string PreviousPortCollisionsKey = "ContainerStartRetry.PreviousPortCollisions";
    private const int MaximumAttempts = 3;
    private const int MaxInfoLength = 16 * 1024;
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(5);

    // Redis and Valkey ship a CLI. The Garnet image ships Bash but has no Redis CLI.
    private static readonly string s_readContainerInfo = """
        if command -v redis-cli >/dev/null; then
            exec redis-cli --raw -p "$1" INFO server
        fi
        if command -v valkey-cli >/dev/null; then
            exec valkey-cli --raw -p "$1" INFO server
        fi
        exec bash -c '
            exec 3<>"/dev/tcp/127.0.0.1/$1" || exit 1
            printf "*2\r\n\$4\r\nINFO\r\n\$6\r\nserver\r\n" >&3
            while IFS= read -r -t 2 line <&3; do
                if [[ $line == run_id:* ]]; then
                    printf "%s\n" "$line"
                    exit 0
                fi
            done
            exit 1
        ' bash "$1"
        """.ReplaceLineEndings("\n");

    internal static Task<TContainer> StartRedisAsync<TContainer>(
        Func<TContainer> createContainer, int redisPort, CancellationToken cancellationToken = default)
        where TContainer : IContainer
    {
        return StartAsync(createContainer, [redisPort], cancellationToken, redisPort);
    }

    /// <summary>Builds and starts a container, retrying host-port bind collisions up to three attempts in total.</summary>
    /// <param name="createContainer">Builds a new, unstarted container on every call.</param>
    /// <param name="containerPorts">The container ports published on random host ports.</param>
    /// <param name="cancellationToken">Cancels the start; no fresh container starts after cancellation.</param>
    internal static Task<TContainer> StartAsync<TContainer>(
        Func<TContainer> createContainer, int[] containerPorts, CancellationToken cancellationToken = default)
        where TContainer : IContainer
    {
        return StartAsync(createContainer, containerPorts, cancellationToken, redisPort: null);
    }

    private static async Task<TContainer> StartAsync<TContainer>(
        Func<TContainer> createContainer, int[] containerPorts, CancellationToken cancellationToken, int? redisPort)
        where TContainer : IContainer
    {
        var collisions = new List<Exception>();

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var container = createContainer();
            bool started = false;

            try
            {
                await container.StartAsync(cancellationToken).ConfigureAwait(false);
                started = true;

                if (redisPort is int port)
                {
                    await VerifyPublishedRedisAsync(container, port, cancellationToken).ConfigureAwait(false);
                }

                return container;
            }
            catch (Exception startupError)
            {
                if (collisions.Count != 0)
                {
                    startupError.Data[PreviousPortCollisionsKey] = collisions.ToArray();
                }

                try
                {
                    await container.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Container startup and cleanup both failed.",
                        collisions.Append(startupError).Append(cleanupError));
                }

                cancellationToken.ThrowIfCancellationRequested();

                bool collision = started
                    ? startupError is PublishedPortCollisionException
                    : ContainerPortCollision.IsStandaloneBindCollision(startupError, containerPorts);

                if (attempt >= MaximumAttempts || !collision)
                {
                    throw;
                }

                collisions.Add(startupError);
            }
        }
    }

    private static async Task VerifyPublishedRedisAsync(IContainer container, int redisPort, CancellationToken cancellationToken)
    {
        string host = container.Hostname;
        int port = container.GetMappedPublicPort(redisPort);
        string endpoint = $"{host}:{port}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(s_probeTimeout);

        try
        {
            string expected = await ReadContainerRunIdAsync(container, redisPort, deadline.Token).ConfigureAwait(false);
            string? actual = await ReadPublishedRunIdAsync(host, port, deadline.Token).ConfigureAwait(false);

            if (actual == expected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            if (actual is null)
            {
                throw new PublishedPortCollisionException($"Published Redis endpoint {endpoint} replied with HTTP.");
            }

            // A server restart also changes run_id. Verify the container still reports the same identity.
            string current = await ReadContainerRunIdAsync(container, redisPort, deadline.Token).ConfigureAwait(false);

            if (current == expected)
            {
                throw new PublishedPortCollisionException(
                    $"Published Redis endpoint {endpoint} reached run_id {actual}, expected {expected}.");
            }

            throw new IOException($"Redis inside the container changed run_id from {expected} to {current} during startup.");
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out verifying published Redis endpoint {endpoint}.", error);
        }
        catch (Exception error) when (error is not (PublishedPortCollisionException or OperationCanceledException))
        {
            throw new IOException($"Could not verify published Redis endpoint {endpoint}.", error);
        }
    }

    private static async Task<string> ReadContainerRunIdAsync(IContainer container, int port, CancellationToken cancellationToken)
    {
        var result = await container.ExecAsync(
            ["sh", "-c", s_readContainerInfo, "redis-info", port.ToString(CultureInfo.InvariantCulture)],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new IOException($"INFO server inside the Redis container exited with code {result.ExitCode}: {result.Stderr.Trim()}");
        }

        return ReadRunId(result.Stdout, "inside the container");
    }

    // A null result means the listener answered with HTTP. Missing or malformed INFO replies throw.
    private static async Task<string?> ReadPublishedRunIdAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        await stream.WriteAsync("*2\r\n$4\r\nINFO\r\n$6\r\nserver\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);

        using var response = new BufferedStream(stream);
        var header = new byte[256];

        for (int length = 1; length <= header.Length; length++)
        {
            await response.ReadExactlyAsync(header.AsMemory(length - 1, 1), cancellationToken).ConfigureAwait(false);

            if (length < 2 || header[length - 2] != '\r' || header[length - 1] != '\n')
            {
                continue;
            }

            string line = Encoding.ASCII.GetString(header, 0, length - 2);

            if (line.StartsWith("HTTP/", StringComparison.Ordinal))
            {
                return null;
            }

            if (!line.StartsWith('$') || !int.TryParse(line.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int size)
                || size <= 0 || size > MaxInfoLength)
            {
                throw new IOException($"Unexpected INFO server reply header: {line}");
            }

            var payload = new byte[size + 2];
            await response.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

            if (!payload.AsSpan(size).SequenceEqual("\r\n"u8))
            {
                throw new IOException("INFO server reply did not end with CRLF.");
            }

            return ReadRunId(Encoding.UTF8.GetString(payload, 0, size), "from the published port");
        }

        throw new IOException("INFO server reply header exceeded 256 bytes.");
    }

    private static string ReadRunId(string info, string source)
    {
        foreach (string line in info.Split('\n'))
        {
            if (line.StartsWith("run_id:", StringComparison.Ordinal) && line.AsSpan(7).Trim().Length > 0)
            {
                return line[7..].Trim();
            }
        }

        throw new IOException($"INFO server {source} did not include a run_id.");
    }

    private sealed class PublishedPortCollisionException(string message) : IOException(message);
}
