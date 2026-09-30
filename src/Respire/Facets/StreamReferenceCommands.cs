using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>Redis 8.2+ handling of consumer-group references during stream removal or trimming.</summary>
public enum StreamReferencePolicy
{
    /// <summary>Remove stream entries while retaining other pending references (KEEPREF).</summary>
    KeepReferences,
    /// <summary>Remove entries and all pending references (DELREF).</summary>
    DeleteReferences,
    /// <summary>Remove only entries read and acknowledged by every consumer group (ACKED).</summary>
    Acknowledged,
}

/// <summary>One XDELEX or XACKDEL outcome, in the same order as the requested IDs.</summary>
public enum RespireStreamDeletionResult
{
    /// <summary>The entry is absent, or XACKDEL found no pending entry in the specified group.</summary>
    NotFound = -1,
    /// <summary>Deletion succeeded. XACKDEL also reports this when it clears an already-deleted pending entry.</summary>
    Deleted = 1,
    /// <summary>ACKED retained the entry because the all-groups condition was not met.</summary>
    Retained = 2,
}

public partial interface IStreamCommands
{
    /// <summary>Removes entries under a reference policy, returning one outcome per ID. Redis 8.2+: XDELEX.</summary>
    ValueTask<RespireStreamDeletionResult[]> RemoveAsync(RespireKey key, StreamReferencePolicy policy,
        params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Removes entries under a reference policy. Redis 8.2+: XDELEX.</summary>
    ValueTask<RespireStreamDeletionResult[]> RemoveAsync(RespireKey key, StreamReferencePolicy policy,
        ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);

    /// <summary>Acknowledges group entries and conditionally removes them. Redis 8.2+: XACKDEL.</summary>
    ValueTask<RespireStreamDeletionResult[]> AcknowledgeAndRemoveAsync(RespireKey key, string group,
        StreamReferencePolicy policy, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Acknowledges group entries and conditionally removes them. Redis 8.2+: XACKDEL.</summary>
    ValueTask<RespireStreamDeletionResult[]> AcknowledgeAndRemoveAsync(RespireKey key, string group,
        StreamReferencePolicy policy, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);
}

internal sealed partial class StreamCommands
{
    private static readonly Verb XDelEx = new("XDELEX");
    private static readonly Verb XAckDel = new("XACKDEL");

    public ValueTask<RespireStreamDeletionResult[]> RemoveAsync(RespireKey key, StreamReferencePolicy policy,
        params ReadOnlySpan<RespireStreamId> ids)
        => RemoveAsync(key, policy, ids, CancellationToken.None);

    public ValueTask<RespireStreamDeletionResult[]> RemoveAsync(RespireKey key, StreamReferencePolicy policy,
        ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
        => client.ConvertResponseAsync("XDELEX", BuildReferenceRemovalCommand(client, key, null, policy, ids),
            cancellationToken, this, static (StreamCommands _, in RespValue value) => ParseDeletionResults(in value));

    public ValueTask<RespireStreamDeletionResult[]> AcknowledgeAndRemoveAsync(RespireKey key, string group,
        StreamReferencePolicy policy, params ReadOnlySpan<RespireStreamId> ids)
        => AcknowledgeAndRemoveAsync(key, group, policy, ids, CancellationToken.None);

    public ValueTask<RespireStreamDeletionResult[]> AcknowledgeAndRemoveAsync(RespireKey key, string group,
        StreamReferencePolicy policy, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        return client.ConvertResponseAsync("XACKDEL", BuildReferenceRemovalCommand(client, key, group, policy, ids),
            cancellationToken, this, static (StreamCommands _, in RespValue value) => ParseDeletionResults(in value));
    }

    internal static string ReferencePolicyToken(StreamReferencePolicy policy) => policy switch
    {
        StreamReferencePolicy.KeepReferences => "KEEPREF",
        StreamReferencePolicy.DeleteReferences => "DELREF",
        StreamReferencePolicy.Acknowledged => "ACKED",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    internal static Cmd1N BuildReferenceRemovalCommand(RespireClient client, RespireKey key, string? group,
        StreamReferencePolicy policy, ReadOnlySpan<RespireStreamId> ids)
    {
        RequireIds(ids);
        var token = ReferencePolicyToken(policy);
        ValidateNumericIds(ids);
        var arguments = new RespireValue[ids.Length + (group is null ? 3 : 4)];
        var index = 0;
        if (group is not null) arguments[index++] = group;
        arguments[index++] = token;
        arguments[index++] = "IDS";
        arguments[index++] = ids.Length;
        foreach (var id in ids) arguments[index++] = id.Value;
        return new Cmd1N(group is null ? XDelEx : XAckDel, client.Key(key), arguments);
    }

    private static void ValidateNumericIds(ReadOnlySpan<RespireStreamId> ids)
    {
        foreach (var id in ids)
        {
            if (id == RespireStreamId.Min || id == RespireStreamId.Max || id == RespireStreamId.New)
                throw new ArgumentException("The command requires numeric stream IDs.", nameof(ids));
            try { _ = id.CompareTo(RespireStreamId.Beginning); }
            catch (FormatException error) { throw new ArgumentException("The command requires numeric stream IDs.", nameof(ids), error); }
        }
    }

    internal static RespireStreamDeletionResult[] ParseDeletionResults(in RespValue value)
    {
        if (value.Type != RespDataType.Array) throw new RespireProtocolException("Expected stream deletion outcomes.");
        var items = value.AsArray();
        var results = new RespireStreamDeletionResult[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            results[i] = ResponseReader.Integer(in items[i]) switch
            {
                -1 => RespireStreamDeletionResult.NotFound,
                1 => RespireStreamDeletionResult.Deleted,
                2 => RespireStreamDeletionResult.Retained,
                _ => throw new RespireProtocolException("Unknown stream deletion outcome."),
            };
        }
        return results;
    }
}
