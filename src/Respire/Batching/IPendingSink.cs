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

internal static class PendingSinkExtensions
{
    internal static void ValidateClusterKeys(this IPendingSink sink, ReadOnlySpan<RespireKey> keys)
    {
        if (sink is RespireTransaction transaction)
        {
            transaction.ValidateClusterKeys(keys);
        }
    }

    internal static void ValidateClusterKeys(
        this IPendingSink sink, RespireKey first, RespireKey second)
    {
        if (sink is RespireTransaction transaction)
        {
            transaction.ValidateClusterKeys(first, second);
        }
    }

    internal static void ValidateClusterKeys(
        this IPendingSink sink, RespireKey first, ReadOnlySpan<RespireKey> rest)
    {
        if (sink is RespireTransaction transaction)
        {
            transaction.ValidateClusterKeys(first, rest);
        }
    }

    internal static void ValidateClusterKeys(
        this IPendingSink sink, ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        if (sink is RespireTransaction transaction)
        {
            transaction.ValidateClusterKeys(pairs);
        }
    }
}
