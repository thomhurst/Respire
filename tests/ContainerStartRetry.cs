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
        if [ "$#" -eq 3 ]; then
            export REDISCLI_AUTH="$3"
        fi
        if command -v redis-cli >/dev/null; then
            exec redis-cli --raw -p "$1" INFO "$2"
        fi
        if command -v valkey-cli >/dev/null; then
            exec valkey-cli --raw -p "$1" INFO "$2"
        fi
        if [ "$2" != server ] || [ "$#" -eq 3 ]; then
            echo "This INFO probe requires a Redis-compatible CLI" >&2
            exit 1
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
        Func<TContainer> createContainer, int redisPort, CancellationToken cancellationToken = default,
        string? password = null, bool useReplicationId = false)
        where TContainer : IContainer
    {
        return StartAsync(createContainer, [redisPort], cancellationToken,
            (container, token) => VerifyPublishedRedisAsync(container, redisPort, token, password, useReplicationId));
    }

    /// <summary>Builds and starts a container, retrying host-port bind collisions up to three attempts in total.</summary>
    /// <param name="createContainer">Builds a new, unstarted container on every call.</param>
    /// <param name="containerPorts">The container ports published on random host ports.</param>
    /// <param name="cancellationToken">Cancels the start; no fresh container starts after cancellation.</param>
    /// <param name="verifyStarted">Optional check before returning the started container.</param>
    internal static async Task<TContainer> StartAsync<TContainer>(
        Func<TContainer> createContainer, int[] containerPorts, CancellationToken cancellationToken = default,
        Func<TContainer, CancellationToken, Task>? verifyStarted = null)
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

                if (verifyStarted is not null)
                {
                    await verifyStarted(container, cancellationToken).ConfigureAwait(false);
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

    internal static async Task VerifyPublishedRedisAsync(
        IContainer container, int redisPort, CancellationToken cancellationToken, string? password = null,
        bool useReplicationId = false, Func<CancellationToken, Task<string>>? readContainerInfo = null)
    {
        string host = container.Hostname;
        int port = container.GetMappedPublicPort(redisPort);
        string endpoint = $"{host}:{port}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(s_probeTimeout);

        // Dragonfly standalone reports a per-server replication ID instead of a run_id.
        string section = useReplicationId ? "replication" : "server";
        string field = useReplicationId ? "master_replid" : "run_id";
        readContainerInfo ??= token => ReadContainerInfoAsync(container, redisPort, section, password, token);

        try
        {
            string expected = ReadIdentity(await readContainerInfo(deadline.Token).ConfigureAwait(false), section, field, "inside the container");
            string? actual = await ReadPublishedIdentityAsync(host, port, section, field, password, deadline.Token).ConfigureAwait(false);

            if (actual == expected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            if (actual is null)
            {
                throw new PublishedPortCollisionException($"Published Redis endpoint {endpoint} replied with HTTP.");
            }

            // A server restart also changes its identity. Check that the container still reports the same one.
            string current = ReadIdentity(await readContainerInfo(deadline.Token).ConfigureAwait(false), section, field, "inside the container");

            if (current == expected)
            {
                throw new PublishedPortCollisionException(
                    $"Published Redis endpoint {endpoint} reached {field} {actual}, expected {expected}.");
            }

            throw new IOException($"Redis inside the container changed {field} from {expected} to {current} during startup.");
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

    private static async Task<string> ReadContainerInfoAsync(
        IContainer container, int port, string section, string? password, CancellationToken cancellationToken)
    {
        string[] command = ["sh", "-c", s_readContainerInfo, "redis-info", port.ToString(CultureInfo.InvariantCulture), section];

        if (password is not null)
        {
            command = [.. command, password];
        }

        ExecResult result = await container.ExecAsync(command, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new IOException($"INFO {section} inside the Redis container failed (exit {result.ExitCode}): {result.Stderr.Trim()}");
        }

        return result.Stdout;
    }

    // A null result means the listener answered with HTTP. Missing or malformed INFO replies throw.
    private static async Task<string?> ReadPublishedIdentityAsync(
        string host, int port, string section, string field, string? password, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        using var response = new BufferedStream(stream);

        if (password is not null)
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"*2\r\n$4\r\nAUTH\r\n${passwordBytes.Length}\r\n"));
            byte[] command = [.. header, .. passwordBytes, (byte)'\r', (byte)'\n'];
            await stream.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            string authentication = await ReadReplyHeaderAsync(response, cancellationToken).ConfigureAwait(false);

            if (authentication.StartsWith("HTTP/", StringComparison.Ordinal))
            {
                return null;
            }

            if (authentication != "+OK")
            {
                throw new IOException("The published listener rejected Redis AUTH or returned an invalid AUTH reply.");
            }
        }

        byte[] infoCommand = Encoding.ASCII.GetBytes(FormattableString.Invariant($"*2\r\n$4\r\nINFO\r\n${section.Length}\r\n{section}\r\n"));
        await stream.WriteAsync(infoCommand, cancellationToken).ConfigureAwait(false);
        string line = await ReadReplyHeaderAsync(response, cancellationToken).ConfigureAwait(false);

        if (line.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return null;
        }

        if (!line.StartsWith('$') || !int.TryParse(line.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int size)
            || size <= 0 || size > MaxInfoLength)
        {
            throw new IOException($"Unexpected INFO {section} reply header: {line}");
        }

        var payload = new byte[size + 2];
        await response.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        if (!payload.AsSpan(size).SequenceEqual("\r\n"u8))
        {
            throw new IOException($"INFO {section} reply did not end with CRLF.");
        }

        return ReadIdentity(Encoding.UTF8.GetString(payload, 0, size), section, field, "from the published port");
    }

    private static async Task<string> ReadReplyHeaderAsync(Stream response, CancellationToken cancellationToken)
    {
        var header = new byte[256];

        for (int length = 1; length <= header.Length; length++)
        {
            await response.ReadExactlyAsync(header.AsMemory(length - 1, 1), cancellationToken).ConfigureAwait(false);

            if (length >= 2 && header[length - 2] == '\r' && header[length - 1] == '\n')
            {
                return Encoding.ASCII.GetString(header, 0, length - 2);
            }
        }

        throw new IOException("Redis probe reply header exceeded 256 bytes.");
    }

    private static string ReadIdentity(string info, string section, string field, string source)
    {
        string prefix = field + ":";

        foreach (string line in info.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal) && line.AsSpan(prefix.Length).Trim().Length > 0)
            {
                return line[prefix.Length..].Trim();
            }
        }

        throw new IOException($"INFO {section} {source} did not include {field}.");
    }

    private sealed class PublishedPortCollisionException(string message) : IOException(message);
}
