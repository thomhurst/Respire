namespace Respire;

/// <summary>Declares how a command affects keys tracked by client-side caching.</summary>
public enum RespireCacheMutation
{
    /// <summary>The command effect is unknown; invalidate the full local cache.</summary>
    Unknown,

    /// <summary>The command does not change keyspace values.</summary>
    ReadOnly,

    /// <summary>The command changes one key.</summary>
    SingleKey,

    /// <summary>The command changes multiple keys with a registered key layout.</summary>
    MultiKey,

    /// <summary>The command changes keyspace values; infer affected keys from its registered layout.</summary>
    Mutation,
}
