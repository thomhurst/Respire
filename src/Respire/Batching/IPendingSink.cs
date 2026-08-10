using Respire.Protocol;

namespace Respire;

/// <summary>
/// The queueing primitive behind the deferred command facets: <see cref="RespireBatch"/> and
/// <see cref="RespireTransaction"/> both implement it, so one set of facet implementations
/// serves both. Implemented explicitly so <c>Add</c> stays off the public surface.
/// </summary>
internal interface IPendingSink
{
    /// <summary>The owning client — supplies key prefixing and serialization.</summary>
    RespireClient Client { get; }

    /// <summary>
    /// Queues <paramref name="command"/> and returns the pending its reply will complete.
    /// <paramref name="convert"/> reads a borrowed reply and must not take ownership of it.
    /// </summary>
    RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand;
}
