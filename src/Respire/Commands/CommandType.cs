namespace Respire.Commands;

/// <summary>
/// Enum representing all Redis command types to avoid delegate allocations
/// </summary>
public enum CommandType : byte
{
    None = 0,
    
    // String commands
    Get,
    Set,
    Del,
    Exists,
    Expire,
    Ttl,
    Incr,
    Decr,
    IncrBy,
    DecrBy,
    Append,
    GetRange,
    SetRange,
    StrLen,
    
    // Hash commands
    HGet,
    HSet,
    HDel,
    HExists,
    HGetAll,
    HIncrBy,
    HKeys,
    HLen,
    HMGet,
    HMSet,
    HVals,
    
    // List commands
    LPush,
    RPush,
    LPop,
    RPop,
    LLen,
    LRange,
    LRem,
    LSet,
    LTrim,
    
    // Set commands
    SAdd,
    SRem,
    SMembers,
    SIsMember,
    SCard,
    
    // Sorted Set commands
    ZAdd,
    ZRem,
    ZRange,
    ZRevRange,
    ZRank,
    ZRevRank,
    ZScore,
    ZCard,
    
    // Connection commands
    Ping,
    Echo,
    Select,
    Auth,
    Quit,
    
    // Transaction commands
    Multi,
    Exec,
    Discard,
    Watch,
    Unwatch,
    
    // Pub/Sub commands
    Subscribe,
    Unsubscribe,
    Publish,
    
    // Server commands
    FlushDb,
    FlushAll,
    DbSize,
    Info,
    Save,
    BgSave,
    LastSave,
    
    // Key commands
    Keys,
    Scan,
    Type,
    Rename,
    RenameNx,
    Persist,
    Move,
    RandomKey
}