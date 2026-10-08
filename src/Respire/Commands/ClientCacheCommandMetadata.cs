using System.Runtime.CompilerServices;

namespace Respire.Commands;

/// <summary>Immutable cache classification; argument-dependent layouts are parsed per invocation.</summary>
internal readonly struct ClientCacheCommandMetadata
{
    // Raw normalization and per-invocation CLIENT checks use the same audited tokens.
    internal const string CachingSubcommand = "CACHING";
    internal const string TrackingSubcommand = "TRACKING";
    internal const string CachingOperation = "CLIENT " + CachingSubcommand;
    internal const string TrackingOperation = "CLIENT " + TrackingSubcommand;

    [Flags]
    private enum Classification : byte
    {
        Initialized = 1, CacheableRead = 2, DisruptsTracking = 4, ClientSubcommand = 8,
    }

    // One atomic word lets copied readonly verbs share a safely published lazy classification.
    private readonly int _packed;

    private ClientCacheCommandMetadata(int packed) => _packed = packed;

    private ClientCacheCommandMetadata(string operation)
    {
        var policy = CommandCacheMutationMetadata.Get(operation);
        var (layout, mutation) = RawCommandKeyLayouts.GetMutationMetadata(operation);
        var classification = Classification.Initialized;
        if (ClientSideCacheCoordinator.CanCacheOperation(operation)) classification |= Classification.CacheableRead;
        if (operation is CachingOperation or TrackingOperation or "HELLO" or "RESET" or "SELECT")
            classification |= Classification.DisruptsTracking;
        if (operation == "CLIENT") classification |= Classification.ClientSubcommand;
        _packed = (byte)policy | ((byte)classification << 8) | ((byte)layout << 16) | ((byte)mutation << 24);
    }

    internal RespireCacheMutation Policy => (RespireCacheMutation)(byte)_packed;
    internal bool IsInitialized => (_packed & ((int)Classification.Initialized << 8)) != 0;
    internal bool CacheableRead => (_packed & ((int)Classification.CacheableRead << 8)) != 0;
    internal bool DisruptsTracking => (_packed & ((int)Classification.DisruptsTracking << 8)) != 0;
    internal bool CheckClientSubcommand => (_packed & ((int)Classification.ClientSubcommand << 8)) != 0;
    internal RawCommandKeyLayouts.LayoutKind ArgumentLayout => (RawCommandKeyLayouts.LayoutKind)(byte)(_packed >> 16);
    internal RawCommandKeyLayouts.MutationKind MutationKind => (RawCommandKeyLayouts.MutationKind)(byte)(_packed >> 24);

    internal static ClientCacheCommandMetadata Get(string operation) => new(operation);

    internal static ClientCacheCommandMetadata GetForWireVerb(string command)
    {
        // Fixed option tokens extend serialization without changing the operation's cache effects.
        var operation = command.ToUpperInvariant() switch
        {
            "SCRIPT FLUSH SYNC" or "SCRIPT FLUSH ASYNC" => "SCRIPT FLUSH",
            var normalized => normalized,
        };
        return Get(operation);
    }

    /// <summary>Shares lazy metadata across readonly verb copies without initializing cache tables for uncached sends.</summary>
    internal sealed class Cache(string command)
    {
        private int _packed;

        internal ClientCacheCommandMetadata Value
        {
            get
            {
                var packed = Volatile.Read(ref _packed);
                return new(packed == 0 ? Initialize() : packed);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int Initialize()
        {
            var packed = GetForWireVerb(command)._packed;
            // Concurrent first users compute the same immutable value; publish all four bytes together.
            Volatile.Write(ref _packed, packed);
            return packed;
        }
    }
}
