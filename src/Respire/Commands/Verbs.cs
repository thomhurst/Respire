using System.Text;

namespace Respire.Commands;

/// <summary>
/// A command verb pre-encoded as RESP bulk strings ("$3\r\nGET\r\n", or two bulks for multi-word
/// verbs like SCRIPT LOAD). Command structs write "*&lt;tokens+args&gt;\r\n" then this verb then
/// the arguments — serialization is one small header plus raw copies.
/// </summary>
internal readonly struct Verb
{
    public readonly byte[] Bulk;
    public readonly int Tokens;

    public Verb(params string[] words)
    {
        Tokens = words.Length;
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            builder.Append('$').Append(word.Length).Append("\r\n").Append(word).Append("\r\n");
        }

        Bulk = Encoding.ASCII.GetBytes(builder.ToString());
    }
}

internal static class Verbs
{
    // Strings
    public static readonly Verb Get = new("GET");
    public static readonly Verb Set = new("SET");
    public static readonly Verb Append = new("APPEND");
    public static readonly Verb StrLen = new("STRLEN");
    public static readonly Verb GetRange = new("GETRANGE");
    public static readonly Verb GetDel = new("GETDEL");
    public static readonly Verb IncrBy = new("INCRBY");
    public static readonly Verb DecrBy = new("DECRBY");
    public static readonly Verb IncrByFloat = new("INCRBYFLOAT");
    public static readonly Verb MGet = new("MGET");
    public static readonly Verb MSet = new("MSET");

    // Keys
    public static readonly Verb Del = new("DEL");
    public static readonly Verb Unlink = new("UNLINK");
    public static readonly Verb Exists = new("EXISTS");
    public static readonly Verb PExpire = new("PEXPIRE");
    public static readonly Verb PExpireAt = new("PEXPIREAT");
    public static readonly Verb Persist = new("PERSIST");
    public static readonly Verb Pttl = new("PTTL");
    public static readonly Verb Type = new("TYPE");
    public static readonly Verb Rename = new("RENAME");
    public static readonly Verb Touch = new("TOUCH");
    public static readonly Verb Scan = new("SCAN");

    // Hashes
    public static readonly Verb HSet = new("HSET");
    public static readonly Verb HGet = new("HGET");
    public static readonly Verb HGetAll = new("HGETALL");
    public static readonly Verb HDel = new("HDEL");
    public static readonly Verb HExists = new("HEXISTS");
    public static readonly Verb HLen = new("HLEN");
    public static readonly Verb HIncrBy = new("HINCRBY");
    public static readonly Verb HIncrByFloat = new("HINCRBYFLOAT");
    public static readonly Verb HKeys = new("HKEYS");
    public static readonly Verb HVals = new("HVALS");
    public static readonly Verb HMGet = new("HMGET");

    // Lists
    public static readonly Verb LPush = new("LPUSH");
    public static readonly Verb RPush = new("RPUSH");
    public static readonly Verb LPop = new("LPOP");
    public static readonly Verb RPop = new("RPOP");
    public static readonly Verb BLPop = new("BLPOP");
    public static readonly Verb BRPop = new("BRPOP");
    public static readonly Verb LLen = new("LLEN");
    public static readonly Verb LRange = new("LRANGE");
    public static readonly Verb LIndex = new("LINDEX");
    public static readonly Verb LRem = new("LREM");
    public static readonly Verb LTrim = new("LTRIM");
    public static readonly Verb LMove = new("LMOVE");
    public static readonly Verb BLMove = new("BLMOVE");

    // Sets
    public static readonly Verb SAdd = new("SADD");
    public static readonly Verb SRem = new("SREM");
    public static readonly Verb SIsMember = new("SISMEMBER");
    public static readonly Verb SCard = new("SCARD");
    public static readonly Verb SMembers = new("SMEMBERS");
    public static readonly Verb SPop = new("SPOP");
    public static readonly Verb SRandMember = new("SRANDMEMBER");
    public static readonly Verb SInter = new("SINTER");
    public static readonly Verb SUnion = new("SUNION");
    public static readonly Verb SDiff = new("SDIFF");
    public static readonly Verb SInterStore = new("SINTERSTORE");
    public static readonly Verb SUnionStore = new("SUNIONSTORE");
    public static readonly Verb SDiffStore = new("SDIFFSTORE");

    // Sorted sets
    public static readonly Verb ZAdd = new("ZADD");
    public static readonly Verb ZScore = new("ZSCORE");
    public static readonly Verb ZIncrBy = new("ZINCRBY");
    public static readonly Verb ZRem = new("ZREM");
    public static readonly Verb ZCard = new("ZCARD");
    public static readonly Verb ZCount = new("ZCOUNT");
    public static readonly Verb ZRank = new("ZRANK");
    public static readonly Verb ZRevRank = new("ZREVRANK");
    public static readonly Verb ZRange = new("ZRANGE");

    // Streams
    public static readonly Verb XAdd = new("XADD");
    public static readonly Verb XLen = new("XLEN");
    public static readonly Verb XRange = new("XRANGE");
    public static readonly Verb XAck = new("XACK");
    public static readonly Verb XGroupCreate = new("XGROUP", "CREATE");
    public static readonly Verb XReadGroup = new("XREADGROUP");

    // Scripts
    public static readonly Verb Eval = new("EVAL");
    public static readonly Verb EvalSha = new("EVALSHA");
    public static readonly Verb ScriptLoad = new("SCRIPT", "LOAD");

    // Transactions
    public static readonly Verb Watch = new("WATCH");

    // Server
    public static readonly Verb Info = new("INFO");
    public static readonly Verb ConfigGet = new("CONFIG", "GET");
    public static readonly Verb ConfigSet = new("CONFIG", "SET");

    // Pub/sub
    public static readonly Verb Publish = new("PUBLISH");
    public static readonly Verb SPublish = new("SPUBLISH");
    public static readonly Verb Subscribe = new("SUBSCRIBE");
    public static readonly Verb Unsubscribe = new("UNSUBSCRIBE");
    public static readonly Verb PSubscribe = new("PSUBSCRIBE");
    public static readonly Verb PUnsubscribe = new("PUNSUBSCRIBE");
    public static readonly Verb SSubscribe = new("SSUBSCRIBE");
    public static readonly Verb SUnsubscribe = new("SUNSUBSCRIBE");
}
