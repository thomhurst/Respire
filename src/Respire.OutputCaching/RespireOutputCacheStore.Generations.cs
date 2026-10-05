using System.Buffers;

namespace Respire.OutputCaching;

public sealed partial class RespireOutputCacheStore
{
    private const string PayloadField = "payload";

    // Index before publication. Lua does not roll back an earlier write if a later
    // command fails. Publishing generation and payload in one HSET keeps an old
    // membership from deleting a replacement even after a partial script failure.
    private static readonly RespireScript PublishGeneration = RespireScript.Create("""
        if not redis.acl_check_cmd('HSET', KEYS[1], 'generation', ARGV[1], 'payload', ARGV[3])
          or not redis.acl_check_cmd('PEXPIREAT', KEYS[1], ARGV[2]) then
          return redis.error_reply('NOPERM output-cache publication requires HSET and PEXPIREAT')
        end
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'none' and kind ~= 'hash' then
          return redis.error_reply('WRONGTYPE invalid output-cache value')
        end
        for i = 2, #KEYS do
          kind = redis.call('TYPE', KEYS[i]).ok
          if kind ~= 'none' and kind ~= 'zset' then
            return redis.error_reply('WRONGTYPE invalid output-cache tag index')
          end
        end
        for i = 3, #KEYS do
          local previous = redis.call('ZSCORE', KEYS[2], ARGV[i + 2])
          if not previous or tonumber(previous) < tonumber(ARGV[2]) then
            redis.call('ZADD', KEYS[2], ARGV[2], ARGV[i + 2])
          end
          redis.call('ZADD', KEYS[i], ARGV[2], ARGV[4])
        end
        redis.call('HSET', KEYS[1], 'generation', ARGV[1], 'payload', ARGV[3])
        redis.call('PEXPIREAT', KEYS[1], ARGV[2])
        return 1
        """);

    private static readonly RespireScript EvictGeneration = RespireScript.Create("""
        if redis.call('HGET', KEYS[1], 'generation') == ARGV[1] then
          redis.call('DEL', KEYS[1])
        end
        redis.call('ZREM', KEYS[2], ARGV[2])
        return 1
        """);

    private async ValueTask SetGenerationAsync(string key, ReadOnlySequence<byte> value,
        ReadOnlyMemory<string> tags, long expires, CancellationToken cancellationToken)
    {
        var generation = Guid.NewGuid().ToString("N");
        RespireKey[] keys = new RespireKey[tags.Length + 2];
        keys[0] = _valuePrefix + key;
        keys[1] = _tagMaster;
        RespireValue[] arguments = new RespireValue[tags.Length + 4];
        arguments[0] = generation;
        arguments[1] = expires;
        arguments[2] = value.ToArray();
        arguments[3] = generation + ":" + key;
        for (var index = 0; index < tags.Length; index++)
        {
            var tag = tags.Span[index];
            keys[index + 2] = _tagPrefix + tag;
            arguments[index + 4] = tag;
        }
        cancellationToken.ThrowIfCancellationRequested();
        _ = await _client.Scripts.ExecuteIntegerAsync(PublishGeneration, keys, arguments, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EvictGenerationsAsync(RespireKey tagKey, List<string> members, CancellationToken cancellationToken)
    {
        if (members.Count == 1)
        {
            var (key, generation) = ParseGenerationMember(members[0]);
            _ = await _client.Scripts.ExecuteIntegerAsync(EvictGeneration,
                [_valuePrefix + key, tagKey], [generation, members[0]], cancellationToken).ConfigureAwait(false);
            return;
        }
        using var batch = _client.CreateBatch();
        foreach (var member in members)
        {
            var (key, generation) = ParseGenerationMember(member);
            _ = batch.Scripts.Evaluate(EvictGeneration, [_valuePrefix + key, tagKey], [generation, member]);
        }
        await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string Key, string Generation) ParseGenerationMember(string member)
    {
        // The fixed-width generation allows arbitrary keys, including colons and
        // empty strings, without delimiter escaping or a reverse tag index.
        if (member.Length < 33 || member[32] != ':' || !Guid.TryParseExact(member.AsSpan(0, 32), "N", out _))
            throw new InvalidDataException("Invalid generation-aware output-cache tag member.");
        return (member[33..], member[..32]);
    }
}
