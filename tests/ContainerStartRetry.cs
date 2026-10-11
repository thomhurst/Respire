using DotNet.Testcontainers.Containers;
using Respire.Testing.Containers;

namespace Respire.Testing;

/// <summary>
/// Starts a Testcontainers container that publishes Docker-assigned (random) host ports, retrying
/// Docker's TCP bind collision on a fresh container.
/// </summary>
/// <remarks>
/// Docker picks a random host port and then listens on it; on a busy host another socket can take the
/// port in between, and the start fails with "failed to listen on TCP socket: address already in use".
/// A container whose start failed cannot be started again, so every attempt builds a new one from
/// <c>createContainer</c> and removes the failed one. Only the structured Docker error recognized by
/// <see cref="ContainerPortCollision.IsStandaloneBindCollision"/> is retried; anything else is rethrown
/// after the failed container is removed. Containers that publish fixed host ports need fresh ports per
/// attempt and use their own retries instead (see <c>RespireContainerFixture</c>).
/// </remarks>
internal static class ContainerStartRetry
{
    internal const string PreviousPortCollisionsKey = "ContainerStartRetry.PreviousPortCollisions";
    private const int MaximumAttempts = 3;

    /// <summary>Builds and starts a container, retrying host-port bind collisions up to three attempts in total.</summary>
    /// <param name="createContainer">Builds a new, unstarted container on every call.</param>
    /// <param name="containerPorts">The container ports published on random host ports.</param>
    /// <param name="cancellationToken">Cancels the start; no fresh container starts after cancellation.</param>
    internal static async Task<TContainer> StartAsync<TContainer>(
        Func<TContainer> createContainer, int[] containerPorts, CancellationToken cancellationToken = default)
        where TContainer : IContainer
    {
        var collisions = new List<Exception>();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var container = createContainer();
            try
            {
                await container.StartAsync(cancellationToken).ConfigureAwait(false);
                return container;
            }
            catch (Exception startupError)
            {
                if (collisions.Count != 0) startupError.Data[PreviousPortCollisionsKey] = collisions.ToArray();
                try { await container.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Container startup and cleanup both failed.",
                        collisions.Append(startupError).Append(cleanupError));
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= MaximumAttempts || !ContainerPortCollision.IsStandaloneBindCollision(startupError, containerPorts))
                    throw;
                collisions.Add(startupError);
            }
        }
    }
}
