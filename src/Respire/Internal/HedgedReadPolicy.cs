using Respire.Networking;

namespace Respire.Internal;

internal static class HedgedReadPolicy
{
    // Read-only is insufficient: scripts, random selections and server-local cursors cannot
    // be assumed to have interchangeable results. New commands require an explicit audit here.
    internal static bool IsEligible(string operation) => operation is
        "GET" or "MGET" or "GETRANGE" or "SUBSTR" or "STRLEN" or "GETBIT" or "BITCOUNT" or "BITPOS" or "BITFIELD_RO"
        or "EXISTS" or "TYPE" or "DUMP" or "TTL" or "PTTL" or "EXPIRETIME" or "PEXPIRETIME"
        or "HGET" or "HMGET" or "HGETALL" or "HEXISTS" or "HLEN" or "HSTRLEN" or "HKEYS" or "HVALS"
        or "HTTL" or "HPTTL" or "HEXPIRETIME" or "HPEXPIRETIME"
        or "LINDEX" or "LLEN" or "LPOS" or "LRANGE" or "LCS"
        or "SCARD" or "SISMEMBER" or "SMISMEMBER" or "SMEMBERS" or "SDIFF" or "SINTER" or "SUNION"
        or "SINTERCARD" or "SDIFFCARD" or "SUNIONCARD"
        or "ZCARD" or "ZCOUNT" or "ZLEXCOUNT" or "ZSCORE" or "ZMSCORE" or "ZRANK" or "ZREVRANK"
        or "ZRANGE" or "ZREVRANGE" or "ZRANGEBYSCORE" or "ZREVRANGEBYSCORE" or "ZRANGEBYLEX" or "ZREVRANGEBYLEX"
        or "ZDIFF" or "ZINTER" or "ZUNION" or "ZINTERCARD"
        or "GEODIST" or "GEOHASH" or "GEOPOS" or "GEOSEARCH" or "GEORADIUS_RO" or "GEORADIUSBYMEMBER_RO"
        or "XLEN" or "XRANGE" or "XREVRANGE";

    internal static bool IsDifferentPeer(RespireConnection first, RespireConnection second)
    {
        var left = first.PeerKey;
        var right = second.PeerKey;
        return left.Port != right.Port || !string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsOriginalEndpoint(RespireEndpoint endpoint, RespireConnection original)
        => endpoint.Port == original.Port && string.Equals(endpoint.Host, original.Host, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port == original.PeerKey.Port && string.Equals(endpoint.Host, original.PeerKey.Host, StringComparison.OrdinalIgnoreCase);
}
