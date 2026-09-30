namespace Respire;

/// <summary>One mutually exclusive XADD idempotency mode. Requires Redis 8.6+.</summary>
/// <remarks>Factories copy binary identifiers. Redis retains deduplication state only within its configured limits.</remarks>
public sealed record StreamIdempotency
{
    private StreamIdempotency(RespireValue producerId, RespireValue idempotentId)
    {
        ProducerId = producerId;
        IdempotentId = idempotentId;
    }

    internal RespireValue ProducerId { get; }
    internal RespireValue IdempotentId { get; }
    internal int ArgumentCount => IdempotentId.IsNull ? 2 : 3;

    /// <summary>Uses an explicit producer/message identity (IDMP), independent of field values.</summary>
    public static StreamIdempotency Manual(RespireValue producerId, RespireValue idempotentId)
        => new(SnapshotIdentifier(producerId, nameof(producerId)), SnapshotIdentifier(idempotentId, nameof(idempotentId)));

    /// <summary>Asks Redis to derive the message identity from the field/value payload (IDMPAUTO).</summary>
    public static StreamIdempotency Automatic(RespireValue producerId)
        => new(SnapshotIdentifier(producerId, nameof(producerId)), default);

    private static RespireValue SnapshotIdentifier(RespireValue value, string parameterName)
    {
        RespireValue.ThrowIfNull(value, parameterName);
        if (value.GetWireLength() == 0) throw new ArgumentException("Idempotency identifiers must not be empty.", parameterName);
        return value.Snapshot();
    }

    internal int CopyArgumentsTo(Span<RespireValue> arguments)
    {
        arguments[0] = IdempotentId.IsNull ? "IDMPAUTO" : "IDMP";
        arguments[1] = ProducerId;
        if (!IdempotentId.IsNull) arguments[2] = IdempotentId;
        return ArgumentCount;
    }
}
