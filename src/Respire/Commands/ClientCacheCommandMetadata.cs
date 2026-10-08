namespace Respire.Commands;

/// <summary>Immutable cache classification; argument-dependent layouts are parsed per invocation.</summary>
internal readonly struct ClientCacheCommandMetadata
{
    [Flags]
    private enum Classification : byte
    {
        Initialized = 1, CacheableRead = 2, DisruptsTracking = 4, ClientSubcommand = 8,
    }

    private readonly byte _policy;
    private readonly Classification _classification;
    internal readonly RawCommandKeyLayouts.LayoutKind ArgumentLayout;
    internal readonly RawCommandKeyLayouts.MutationKind MutationKind;

    private ClientCacheCommandMetadata(string operation)
    {
        _policy = (byte)CommandCacheMutationMetadata.Get(operation);
        (ArgumentLayout, MutationKind) = RawCommandKeyLayouts.GetMutationMetadata(operation);
        _classification = Classification.Initialized;
        if (ClientSideCacheCoordinator.CanCacheOperation(operation)) _classification |= Classification.CacheableRead;
        if (operation is "CLIENT CACHING" or "CLIENT TRACKING" or "HELLO" or "RESET" or "SELECT")
            _classification |= Classification.DisruptsTracking;
        if (operation == "CLIENT") _classification |= Classification.ClientSubcommand;
    }

    internal RespireCacheMutation Policy => (RespireCacheMutation)_policy;
    internal bool IsInitialized => (_classification & Classification.Initialized) != 0;
    internal bool CacheableRead => (_classification & Classification.CacheableRead) != 0;
    internal bool DisruptsTracking => (_classification & Classification.DisruptsTracking) != 0;
    internal bool CheckClientSubcommand => (_classification & Classification.ClientSubcommand) != 0;

    internal static ClientCacheCommandMetadata Get(string operation) => new(operation);
}
