namespace Respire.Coordination;

/// <summary>Verifies the read and invalidation guarantees needed before any wait commands run.</summary>
internal static class CoordinationWaitAdmission
{
    internal static IRespireClientSideCache Validate(
        IRespireClient client, params ReadOnlySpan<RespireKey> physicalKeys)
    {
        // The public client/cache interfaces cannot attest to routing, cache bypass, or tracking.
        // A forwarded cache alone does not prove that a decorator's reads use that cache.
        if (client is not RespireClient concrete)
            throw new RespireConfigurationException(
                "Coordination wait read routing and tracking cannot be verified for custom clients or decorators. Use a RespireClient or its built-in views.");
        if (concrete.GetBatchReadFromPolicy() != RespireReadFrom.Primary)
            throw new RespireConfigurationException(
                "Coordination waits require primary reads so tracking observes lease and queue writes.");
        var cache = concrete.ReadCache ?? throw new RespireConfigurationException(
            "Coordination waits require RESP3 client-side caching and tracking without cache bypass.");
        if (cache.NoLoop)
            throw new RespireConfigurationException(
                "Coordination waits require ClientSideCache.NoLoop=false so same-client releases wake waiters.");
        foreach (var key in physicalKeys)
        {
            if (!cache.CanTrack(key))
                throw new RespireConfigurationException(
                    "Coordination waits require cache tracking coverage for every physical lease and queue key.");
        }
        return cache;
    }
}
