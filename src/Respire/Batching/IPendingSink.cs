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

    RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command, ReadOnlySpan<RespireKey> keys,
        Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand;

    RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command, RespireKey first, RespireKey second,
        Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand;

    RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command, RespireKey first, ReadOnlySpan<RespireKey> rest,
        Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand;

    RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs,
        Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand;
}
