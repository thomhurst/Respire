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

    /// <summary>Validates a key before a multi-key command is queued.</summary>
    void ValidateClusterKey(in RespireKey key)
    {
    }

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
        foreach (ref readonly var key in keys)
        {
            sink.ValidateClusterKey(in key);
        }
    }

    internal static void ValidateClusterKeys(
        this IPendingSink sink, RespireKey first, RespireKey second)
    {
        sink.ValidateClusterKey(in first);
        sink.ValidateClusterKey(in second);
    }

    internal static void ValidateClusterKeys(
        this IPendingSink sink, RespireKey first, ReadOnlySpan<RespireKey> rest)
    {
        sink.ValidateClusterKey(in first);
        sink.ValidateClusterKeys(rest);
    }

    internal static void ValidateClusterKeys(
        this IPendingSink sink, ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        foreach (ref readonly var pair in pairs)
        {
            sink.ValidateClusterKey(in pair.Key);
        }
    }
}
