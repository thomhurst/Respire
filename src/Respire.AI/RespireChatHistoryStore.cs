using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Respire.AI;

/// <summary>Stores ordered Microsoft.Extensions.AI messages in bounded, expiring Redis lists.</summary>
/// <remarks>The caller owns the Redis client. Concurrent append batches remain contiguous in Redis execution order.
/// Retrying an append after an ambiguous network failure can duplicate messages; appends are not idempotent.</remarks>
public sealed class RespireChatHistoryStore
{
    private static readonly RespireScript Append = RespireScript.Create("""
        for i = 3, #ARGV do
            redis.call('RPUSH', KEYS[1], ARGV[i])
        end
        redis.call('LTRIM', KEYS[1], -tonumber(ARGV[1]), -1)
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return 1
        """);

    private readonly IRespireClient _client;
    private readonly RespireChatHistoryOptions _options;
    private readonly string _prefix;

    /// <summary>Creates a store with an application/tenant namespace and retention settings.</summary>
    public RespireChatHistoryStore(IRespireClient client, RespireChatHistoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _options = options ?? new();
        _options.Validate();
        _client = client;
        _prefix = "respire:history:v1:" + Hash(_options.StoreName) + ":";
    }

    /// <summary>Atomically appends a batch, keeps the newest configured messages, and renews expiry.
    /// An empty batch does not create or refresh a conversation.</summary>
    public async ValueTask AppendAsync(string conversationId, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId);
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        // Serialize the entire batch before touching Redis, so unsupported content cannot leave a partial append.
        var arguments = new List<RespireValue>
        {
            _options.MaximumMessages,
            _options.TimeToLive.Ticks / TimeSpan.TicksPerMillisecond,
        };
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            arguments.Add(JsonSerializer.SerializeToUtf8Bytes(message, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatMessage))));
        }
        if (arguments.Count == 2) return;
        using var result = await _client.Scripts.ExecuteLeaseAsync(Append, [key], arguments.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns up to the newest <paramref name="maximumMessages"/> messages, oldest first. Reads do not renew expiry.</summary>
    public async ValueTask<IReadOnlyList<ChatMessage>> ReadAsync(string conversationId, int maximumMessages = 100, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessages);
        var json = await _client.Lists.RangeAsync(key, -maximumMessages, -1, cancellationToken).ConfigureAwait(false);
        var messages = new ChatMessage[json.Length];
        for (var i = 0; i < json.Length; i++)
            messages[i] = (ChatMessage?)JsonSerializer.Deserialize(json[i], AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatMessage)))
                ?? throw new JsonException("A stored chat message is null.");
        return messages;
    }

    /// <summary>Keeps the newest messages without renewing expiry. Zero clears the conversation.</summary>
    public async ValueTask TrimAsync(string conversationId, int maximumMessages, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMessages);
        if (maximumMessages == 0)
            await _client.Keys.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        else
            await _client.Lists.TrimAsync(key, -maximumMessages, -1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the remaining lifetime of an existing conversation. Returns false when it does not exist.</summary>
    public async ValueTask<bool> ExpireAsync(string conversationId, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId);
        if (timeToLive.Ticks < TimeSpan.TicksPerMillisecond)
            throw new ArgumentOutOfRangeException(nameof(timeToLive));
        return await _client.Keys.ExpireAsync(key, timeToLive, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the conversation.</summary>
    public async ValueTask ClearAsync(string conversationId, CancellationToken cancellationToken = default)
        => await _client.Keys.DeleteAsync(GetKey(conversationId), cancellationToken).ConfigureAwait(false);

    private RespireKey GetKey(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return _prefix + Hash(conversationId);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
