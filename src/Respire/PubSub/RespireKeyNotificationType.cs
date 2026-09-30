namespace Respire;

/// <summary>A known Redis notification event. Unknown events retain their raw bytes.</summary>
public enum RespireKeyNotificationType
{
    /// <summary>An unrecognized event, including future server and module events.</summary>
    Unknown,
    /// <summary>The Redis <c>append</c> event.</summary>
    Append,
    /// <summary>The Redis <c>ardel</c> event.</summary>
    ArDel,
    /// <summary>The Redis <c>ardelrange</c> event.</summary>
    ArDelRange,
    /// <summary>The Redis <c>arinsert</c> event.</summary>
    ArInsert,
    /// <summary>The Redis <c>armset</c> event.</summary>
    ArMSet,
    /// <summary>The Redis <c>arring</c> event.</summary>
    ArRing,
    /// <summary>The Redis <c>arseek</c> event.</summary>
    ArSeek,
    /// <summary>The Redis <c>arset</c> event.</summary>
    ArSet,
    /// <summary>The Redis <c>copy_to</c> event.</summary>
    CopyTo,
    /// <summary>The Redis <c>del</c> event.</summary>
    Del,
    /// <summary>The Redis <c>evicted</c> event.</summary>
    Evicted,
    /// <summary>The Redis <c>expire</c> event.</summary>
    Expire,
    /// <summary>The Redis <c>expired</c> event.</summary>
    Expired,
    /// <summary>The Redis <c>hdel</c> event.</summary>
    HDel,
    /// <summary>The Redis <c>hexpire</c> event.</summary>
    HExpire,
    /// <summary>The Redis <c>hexpired</c> event.</summary>
    HExpired,
    /// <summary>The Redis <c>hincrby</c> event.</summary>
    HIncrBy,
    /// <summary>The Redis <c>hincrbyfloat</c> event.</summary>
    HIncrByFloat,
    /// <summary>The Redis <c>hpersist</c> event.</summary>
    HPersist,
    /// <summary>The Redis <c>hset</c> event.</summary>
    HSet,
    /// <summary>The Redis <c>incrby</c> event.</summary>
    IncrBy,
    /// <summary>The Redis <c>incrbyfloat</c> event.</summary>
    IncrByFloat,
    /// <summary>The Redis <c>keymiss</c> event.</summary>
    KeyMiss,
    /// <summary>The Redis <c>linsert</c> event.</summary>
    LInsert,
    /// <summary>The Redis <c>lpop</c> event.</summary>
    LPop,
    /// <summary>The Redis <c>lpush</c> event.</summary>
    LPush,
    /// <summary>The Redis <c>lrem</c> event.</summary>
    LRem,
    /// <summary>The Redis <c>lset</c> event.</summary>
    LSet,
    /// <summary>The Redis <c>ltrim</c> event.</summary>
    LTrim,
    /// <summary>The Redis <c>move_from</c> event.</summary>
    MoveFrom,
    /// <summary>The Redis <c>move_to</c> event.</summary>
    MoveTo,
    /// <summary>The Redis <c>new</c> event.</summary>
    New,
    /// <summary>The Redis <c>overwritten</c> event.</summary>
    Overwritten,
    /// <summary>The Redis <c>persist</c> event.</summary>
    Persist,
    /// <summary>The Redis <c>pfadd</c> event.</summary>
    PfAdd,
    /// <summary>The Redis <c>pfmerge</c> event.</summary>
    PfMerge,
    /// <summary>The Redis <c>rename_from</c> event.</summary>
    RenameFrom,
    /// <summary>The Redis <c>rename_to</c> event.</summary>
    RenameTo,
    /// <summary>The Redis <c>restore</c> event.</summary>
    Restore,
    /// <summary>The Redis <c>rpop</c> event.</summary>
    RPop,
    /// <summary>The Redis <c>rpush</c> event.</summary>
    RPush,
    /// <summary>The Redis <c>sadd</c> event.</summary>
    SAdd,
    /// <summary>The Redis <c>sdiffstore</c> event.</summary>
    SDiffStore,
    /// <summary>The Redis <c>set</c> event.</summary>
    Set,
    /// <summary>The Redis <c>setbit</c> event.</summary>
    SetBit,
    /// <summary>The Redis <c>setrange</c> event.</summary>
    SetRange,
    /// <summary>The Redis <c>sinterstore</c> event.</summary>
    SInterStore,
    /// <summary>The Redis <c>sortstore</c> event.</summary>
    SortStore,
    /// <summary>The Redis <c>spop</c> event.</summary>
    SPop,
    /// <summary>The Redis <c>srem</c> event.</summary>
    SRem,
    /// <summary>The Redis <c>sunionstore</c> event.</summary>
    SUnionStore,
    /// <summary>The Redis <c>type_changed</c> event.</summary>
    TypeChanged,
    /// <summary>The Redis <c>xadd</c> event.</summary>
    XAdd,
    /// <summary>The Redis <c>xdel</c> event.</summary>
    XDel,
    /// <summary>The Redis <c>xgroup-create</c> event.</summary>
    XGroupCreate,
    /// <summary>The Redis <c>xgroup-createconsumer</c> event.</summary>
    XGroupCreateConsumer,
    /// <summary>The Redis <c>xgroup-delconsumer</c> event.</summary>
    XGroupDelConsumer,
    /// <summary>The Redis <c>xgroup-destroy</c> event.</summary>
    XGroupDestroy,
    /// <summary>The Redis <c>xgroup-setid</c> event.</summary>
    XGroupSetId,
    /// <summary>The Redis <c>xsetid</c> event.</summary>
    XSetId,
    /// <summary>The Redis <c>xtrim</c> event.</summary>
    XTrim,
    /// <summary>The Redis <c>zadd</c> event.</summary>
    ZAdd,
    /// <summary>The Redis <c>zdiffstore</c> event.</summary>
    ZDiffStore,
    /// <summary>The Redis <c>zincr</c> event.</summary>
    ZIncr,
    /// <summary>The Redis <c>zinterstore</c> event.</summary>
    ZInterStore,
    /// <summary>The Redis <c>zpopmax</c> event.</summary>
    ZPopMax,
    /// <summary>The Redis <c>zpopmin</c> event.</summary>
    ZPopMin,
    /// <summary>The Redis <c>zrangestore</c> event.</summary>
    ZRangeStore,
    /// <summary>The Redis <c>zrem</c> event.</summary>
    ZRem,
    /// <summary>The Redis <c>zrembylex</c> event.</summary>
    ZRemByLex,
    /// <summary>The Redis <c>zrembyrank</c> event.</summary>
    ZRemByRank,
    /// <summary>The Redis <c>zrembyscore</c> event.</summary>
    ZRemByScore,
    /// <summary>The Redis <c>zunionstore</c> event.</summary>
    ZUnionStore,
}

internal static class KeyNotificationTypes
{
    internal static RespireKeyNotificationType Parse(ReadOnlySpan<byte> value)
    {
        switch (value.Length)
        {
            case 3:
                if (value.SequenceEqual("del"u8)) return RespireKeyNotificationType.Del;
                if (value.SequenceEqual("new"u8)) return RespireKeyNotificationType.New;
                if (value.SequenceEqual("set"u8)) return RespireKeyNotificationType.Set;
                break;
            case 4:
                if (value.SequenceEqual("hdel"u8)) return RespireKeyNotificationType.HDel;
                if (value.SequenceEqual("hset"u8)) return RespireKeyNotificationType.HSet;
                if (value.SequenceEqual("lpop"u8)) return RespireKeyNotificationType.LPop;
                if (value.SequenceEqual("lrem"u8)) return RespireKeyNotificationType.LRem;
                if (value.SequenceEqual("lset"u8)) return RespireKeyNotificationType.LSet;
                if (value.SequenceEqual("rpop"u8)) return RespireKeyNotificationType.RPop;
                if (value.SequenceEqual("sadd"u8)) return RespireKeyNotificationType.SAdd;
                if (value.SequenceEqual("spop"u8)) return RespireKeyNotificationType.SPop;
                if (value.SequenceEqual("srem"u8)) return RespireKeyNotificationType.SRem;
                if (value.SequenceEqual("xadd"u8)) return RespireKeyNotificationType.XAdd;
                if (value.SequenceEqual("xdel"u8)) return RespireKeyNotificationType.XDel;
                if (value.SequenceEqual("zadd"u8)) return RespireKeyNotificationType.ZAdd;
                if (value.SequenceEqual("zrem"u8)) return RespireKeyNotificationType.ZRem;
                break;
            case 5:
                if (value.SequenceEqual("ardel"u8)) return RespireKeyNotificationType.ArDel;
                if (value.SequenceEqual("arset"u8)) return RespireKeyNotificationType.ArSet;
                if (value.SequenceEqual("lpush"u8)) return RespireKeyNotificationType.LPush;
                if (value.SequenceEqual("ltrim"u8)) return RespireKeyNotificationType.LTrim;
                if (value.SequenceEqual("pfadd"u8)) return RespireKeyNotificationType.PfAdd;
                if (value.SequenceEqual("rpush"u8)) return RespireKeyNotificationType.RPush;
                if (value.SequenceEqual("xtrim"u8)) return RespireKeyNotificationType.XTrim;
                if (value.SequenceEqual("zincr"u8)) return RespireKeyNotificationType.ZIncr;
                break;
            case 6:
                if (value.SequenceEqual("append"u8)) return RespireKeyNotificationType.Append;
                if (value.SequenceEqual("armset"u8)) return RespireKeyNotificationType.ArMSet;
                if (value.SequenceEqual("arring"u8)) return RespireKeyNotificationType.ArRing;
                if (value.SequenceEqual("arseek"u8)) return RespireKeyNotificationType.ArSeek;
                if (value.SequenceEqual("expire"u8)) return RespireKeyNotificationType.Expire;
                if (value.SequenceEqual("incrby"u8)) return RespireKeyNotificationType.IncrBy;
                if (value.SequenceEqual("setbit"u8)) return RespireKeyNotificationType.SetBit;
                if (value.SequenceEqual("xsetid"u8)) return RespireKeyNotificationType.XSetId;
                break;
            case 7:
                if (value.SequenceEqual("copy_to"u8)) return RespireKeyNotificationType.CopyTo;
                if (value.SequenceEqual("evicted"u8)) return RespireKeyNotificationType.Evicted;
                if (value.SequenceEqual("expired"u8)) return RespireKeyNotificationType.Expired;
                if (value.SequenceEqual("hexpire"u8)) return RespireKeyNotificationType.HExpire;
                if (value.SequenceEqual("hincrby"u8)) return RespireKeyNotificationType.HIncrBy;
                if (value.SequenceEqual("keymiss"u8)) return RespireKeyNotificationType.KeyMiss;
                if (value.SequenceEqual("linsert"u8)) return RespireKeyNotificationType.LInsert;
                if (value.SequenceEqual("move_to"u8)) return RespireKeyNotificationType.MoveTo;
                if (value.SequenceEqual("persist"u8)) return RespireKeyNotificationType.Persist;
                if (value.SequenceEqual("pfmerge"u8)) return RespireKeyNotificationType.PfMerge;
                if (value.SequenceEqual("restore"u8)) return RespireKeyNotificationType.Restore;
                if (value.SequenceEqual("zpopmax"u8)) return RespireKeyNotificationType.ZPopMax;
                if (value.SequenceEqual("zpopmin"u8)) return RespireKeyNotificationType.ZPopMin;
                break;
            case 8:
                if (value.SequenceEqual("arinsert"u8)) return RespireKeyNotificationType.ArInsert;
                if (value.SequenceEqual("hexpired"u8)) return RespireKeyNotificationType.HExpired;
                if (value.SequenceEqual("hpersist"u8)) return RespireKeyNotificationType.HPersist;
                if (value.SequenceEqual("setrange"u8)) return RespireKeyNotificationType.SetRange;
                break;
            case 9:
                if (value.SequenceEqual("move_from"u8)) return RespireKeyNotificationType.MoveFrom;
                if (value.SequenceEqual("rename_to"u8)) return RespireKeyNotificationType.RenameTo;
                if (value.SequenceEqual("sortstore"u8)) return RespireKeyNotificationType.SortStore;
                if (value.SequenceEqual("zrembylex"u8)) return RespireKeyNotificationType.ZRemByLex;
                break;
            case 10:
                if (value.SequenceEqual("ardelrange"u8)) return RespireKeyNotificationType.ArDelRange;
                if (value.SequenceEqual("sdiffstore"u8)) return RespireKeyNotificationType.SDiffStore;
                if (value.SequenceEqual("zdiffstore"u8)) return RespireKeyNotificationType.ZDiffStore;
                if (value.SequenceEqual("zrembyrank"u8)) return RespireKeyNotificationType.ZRemByRank;
                break;
            case 11:
                if (value.SequenceEqual("incrbyfloat"u8)) return RespireKeyNotificationType.IncrByFloat;
                if (value.SequenceEqual("overwritten"u8)) return RespireKeyNotificationType.Overwritten;
                if (value.SequenceEqual("rename_from"u8)) return RespireKeyNotificationType.RenameFrom;
                if (value.SequenceEqual("sinterstore"u8)) return RespireKeyNotificationType.SInterStore;
                if (value.SequenceEqual("sunionstore"u8)) return RespireKeyNotificationType.SUnionStore;
                if (value.SequenceEqual("zinterstore"u8)) return RespireKeyNotificationType.ZInterStore;
                if (value.SequenceEqual("zrangestore"u8)) return RespireKeyNotificationType.ZRangeStore;
                if (value.SequenceEqual("zrembyscore"u8)) return RespireKeyNotificationType.ZRemByScore;
                if (value.SequenceEqual("zunionstore"u8)) return RespireKeyNotificationType.ZUnionStore;
                break;
            case 12:
                if (value.SequenceEqual("hincrbyfloat"u8)) return RespireKeyNotificationType.HIncrByFloat;
                if (value.SequenceEqual("type_changed"u8)) return RespireKeyNotificationType.TypeChanged;
                if (value.SequenceEqual("xgroup-setid"u8)) return RespireKeyNotificationType.XGroupSetId;
                break;
            case 13:
                if (value.SequenceEqual("xgroup-create"u8)) return RespireKeyNotificationType.XGroupCreate;
                break;
            case 14:
                if (value.SequenceEqual("xgroup-destroy"u8)) return RespireKeyNotificationType.XGroupDestroy;
                break;
            case 18:
                if (value.SequenceEqual("xgroup-delconsumer"u8)) return RespireKeyNotificationType.XGroupDelConsumer;
                break;
            case 21:
                if (value.SequenceEqual("xgroup-createconsumer"u8)) return RespireKeyNotificationType.XGroupCreateConsumer;
                break;
        }
        return RespireKeyNotificationType.Unknown;
    }

    internal static ReadOnlySpan<byte> Format(RespireKeyNotificationType value) => value switch
    {
        RespireKeyNotificationType.Append => "append"u8,
        RespireKeyNotificationType.ArDel => "ardel"u8,
        RespireKeyNotificationType.ArDelRange => "ardelrange"u8,
        RespireKeyNotificationType.ArInsert => "arinsert"u8,
        RespireKeyNotificationType.ArMSet => "armset"u8,
        RespireKeyNotificationType.ArRing => "arring"u8,
        RespireKeyNotificationType.ArSeek => "arseek"u8,
        RespireKeyNotificationType.ArSet => "arset"u8,
        RespireKeyNotificationType.CopyTo => "copy_to"u8,
        RespireKeyNotificationType.Del => "del"u8,
        RespireKeyNotificationType.Evicted => "evicted"u8,
        RespireKeyNotificationType.Expire => "expire"u8,
        RespireKeyNotificationType.Expired => "expired"u8,
        RespireKeyNotificationType.HDel => "hdel"u8,
        RespireKeyNotificationType.HExpire => "hexpire"u8,
        RespireKeyNotificationType.HExpired => "hexpired"u8,
        RespireKeyNotificationType.HIncrBy => "hincrby"u8,
        RespireKeyNotificationType.HIncrByFloat => "hincrbyfloat"u8,
        RespireKeyNotificationType.HPersist => "hpersist"u8,
        RespireKeyNotificationType.HSet => "hset"u8,
        RespireKeyNotificationType.IncrBy => "incrby"u8,
        RespireKeyNotificationType.IncrByFloat => "incrbyfloat"u8,
        RespireKeyNotificationType.KeyMiss => "keymiss"u8,
        RespireKeyNotificationType.LInsert => "linsert"u8,
        RespireKeyNotificationType.LPop => "lpop"u8,
        RespireKeyNotificationType.LPush => "lpush"u8,
        RespireKeyNotificationType.LRem => "lrem"u8,
        RespireKeyNotificationType.LSet => "lset"u8,
        RespireKeyNotificationType.LTrim => "ltrim"u8,
        RespireKeyNotificationType.MoveFrom => "move_from"u8,
        RespireKeyNotificationType.MoveTo => "move_to"u8,
        RespireKeyNotificationType.New => "new"u8,
        RespireKeyNotificationType.Overwritten => "overwritten"u8,
        RespireKeyNotificationType.Persist => "persist"u8,
        RespireKeyNotificationType.PfAdd => "pfadd"u8,
        RespireKeyNotificationType.PfMerge => "pfmerge"u8,
        RespireKeyNotificationType.RenameFrom => "rename_from"u8,
        RespireKeyNotificationType.RenameTo => "rename_to"u8,
        RespireKeyNotificationType.Restore => "restore"u8,
        RespireKeyNotificationType.RPop => "rpop"u8,
        RespireKeyNotificationType.RPush => "rpush"u8,
        RespireKeyNotificationType.SAdd => "sadd"u8,
        RespireKeyNotificationType.SDiffStore => "sdiffstore"u8,
        RespireKeyNotificationType.Set => "set"u8,
        RespireKeyNotificationType.SetBit => "setbit"u8,
        RespireKeyNotificationType.SetRange => "setrange"u8,
        RespireKeyNotificationType.SInterStore => "sinterstore"u8,
        RespireKeyNotificationType.SortStore => "sortstore"u8,
        RespireKeyNotificationType.SPop => "spop"u8,
        RespireKeyNotificationType.SRem => "srem"u8,
        RespireKeyNotificationType.SUnionStore => "sunionstore"u8,
        RespireKeyNotificationType.TypeChanged => "type_changed"u8,
        RespireKeyNotificationType.XAdd => "xadd"u8,
        RespireKeyNotificationType.XDel => "xdel"u8,
        RespireKeyNotificationType.XGroupCreate => "xgroup-create"u8,
        RespireKeyNotificationType.XGroupCreateConsumer => "xgroup-createconsumer"u8,
        RespireKeyNotificationType.XGroupDelConsumer => "xgroup-delconsumer"u8,
        RespireKeyNotificationType.XGroupDestroy => "xgroup-destroy"u8,
        RespireKeyNotificationType.XGroupSetId => "xgroup-setid"u8,
        RespireKeyNotificationType.XSetId => "xsetid"u8,
        RespireKeyNotificationType.XTrim => "xtrim"u8,
        RespireKeyNotificationType.ZAdd => "zadd"u8,
        RespireKeyNotificationType.ZDiffStore => "zdiffstore"u8,
        RespireKeyNotificationType.ZIncr => "zincr"u8,
        RespireKeyNotificationType.ZInterStore => "zinterstore"u8,
        RespireKeyNotificationType.ZPopMax => "zpopmax"u8,
        RespireKeyNotificationType.ZPopMin => "zpopmin"u8,
        RespireKeyNotificationType.ZRangeStore => "zrangestore"u8,
        RespireKeyNotificationType.ZRem => "zrem"u8,
        RespireKeyNotificationType.ZRemByLex => "zrembylex"u8,
        RespireKeyNotificationType.ZRemByRank => "zrembyrank"u8,
        RespireKeyNotificationType.ZRemByScore => "zrembyscore"u8,
        RespireKeyNotificationType.ZUnionStore => "zunionstore"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Use raw event bytes for unknown notification types."),
    };
}
