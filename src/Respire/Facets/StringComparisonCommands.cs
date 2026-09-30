using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

public partial interface IStringCommands
{
    /// <summary>Sets a raw value only when the comparison succeeds, returning whether it was written. Redis: SET with a comparison condition.</summary>
    /// <remarks>Binary value buffers must remain unchanged until completion. A failed comparison preserves the old value and expiry.</remarks>
    ValueTask<bool> SetConditionalAsync(RespireKey key, RespireValue value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default);

    /// <summary>Sets a serialized value only when the raw comparison succeeds, returning whether it was written.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<bool> SetConditionalAsync<T>(RespireKey key, T value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default);

    /// <summary>Attempts a conditional SET and returns the old string, including when the comparison fails. Redis: SET with GET.</summary>
    /// <remarks>A null result means the key was missing. The result is not a write-success flag.</remarks>
    ValueTask<string?> GetAndSetConditionalAsync(RespireKey key, RespireValue value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default);

    /// <summary>Attempts a conditional serialized SET and deserializes the old value, including when the raw comparison fails.</summary>
    /// <remarks>The result is not a write-success flag. Binary results own their data.</remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAndSetConditionalAsync<T>(RespireKey key, T value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default);

    /// <summary>Deletes a string only when the comparison succeeds. Returns false for missing keys or failed comparisons. Redis 8.4+: DELEX.</summary>
    /// <remarks>For Valkey 9.0+ byte equality, use <see cref="DeleteIfEqualAsync"/>. The command is never substituted based on server capabilities.</remarks>
    ValueTask<bool> DeleteConditionalAsync(RespireKey key, RespireValueCondition condition, CancellationToken cancellationToken = default);

    /// <summary>Deletes a string only when its bytes equal the raw operand. Returns false for missing keys or mismatches. Valkey 9.0+: DELIFEQ.</summary>
    /// <remarks>This explicitly uses DELIFEQ; use <see cref="DeleteConditionalAsync"/> with <see cref="RespireValueCondition.EqualTo"/> for Redis DELEX. Binary operands are borrowed until completion.</remarks>
    ValueTask<bool> DeleteIfEqualAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>Returns the owned hexadecimal digest of a string, or null for a missing key. Redis 8.4+: DIGEST.</summary>
    ValueTask<string?> DigestAsync(RespireKey key, CancellationToken cancellationToken = default);
}

internal sealed partial class StringCommands
{
    public ValueTask<bool> SetConditionalAsync(RespireKey key, RespireValue value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default)
        => client.OkOrNullAsync("SET", CompareSetCommand(client, key, value, condition, expiry, false), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetConditionalAsync<T>(RespireKey key, T value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default)
        => SetConditionalAsync(key, client.Serialize(value), condition, expiry, cancellationToken);

    public ValueTask<string?> GetAndSetConditionalAsync(RespireKey key, RespireValue value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("SET", CompareSetCommand(client, key, value, condition, expiry, true), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAndSetConditionalAsync<T>(RespireKey key, T value, RespireValueCondition condition,
        RespireExpiry expiry = default, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, ConditionalSetCommand>("SET",
            CompareSetCommand(client, key, client.Serialize(value), condition, expiry, true), cancellationToken);

    public ValueTask<bool> DeleteConditionalAsync(RespireKey key, RespireValueCondition condition, CancellationToken cancellationToken = default)
        => client.FlagAsync("DELEX", CompareDeleteCommand(client, key, condition), cancellationToken);

    public ValueTask<bool> DeleteIfEqualAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default)
        => client.FlagAsync("DELIFEQ", DeleteIfEqualCommand(client, key, value), cancellationToken);

    public ValueTask<string?> DigestAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("DIGEST", new Cmd1(RespireCommands.String.DIGEST.Verb, client.Key(in key)), cancellationToken);

    internal static ConditionalSetCommand CompareSetCommand(RespireClient client, RespireKey key, RespireValue value,
        RespireValueCondition condition, RespireExpiry expiry, bool returnOld)
    {
        ArgumentNullException.ThrowIfNull(condition);
        RespireValue.ThrowIfNull(value, nameof(value));
        SetCommand.ValidateExpiry(expiry);
        return new(client.Key(in key), value, condition, expiry, returnOld);
    }

    internal static Cmd3 CompareDeleteCommand(RespireClient client, RespireKey key, RespireValueCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new(RespireCommands.String.DELEX.Verb, client.Key(in key), condition.Token, condition.Operand);
    }

    internal static Cmd2 DeleteIfEqualCommand(RespireClient client, RespireKey key, RespireValue value)
    {
        RespireValue.ThrowIfNull(value, nameof(value));
        return new(RespireCommands.String.DELIFEQ.Verb, client.Key(in key), value);
    }
}
