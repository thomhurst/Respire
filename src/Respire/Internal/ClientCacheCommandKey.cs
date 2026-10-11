namespace Respire.Internal;

/// <summary>
/// Allocation-free lookup identity for a command invocation. Stored identities snapshot their
/// arguments so caller-owned binary memory can safely be used by the cache.
/// </summary>
internal readonly struct ClientCacheCommandKey : IEquatable<ClientCacheCommandKey>
{
    private readonly string _operation;
    private readonly RespireValue _a1;
    private readonly RespireValue _a2;
    private readonly RespireValue _a3;
    private readonly RespireValue _a4;
    private readonly RespireValue _a5;
    private readonly RespireValue[]? _rest;
    private readonly byte _prefixCount;
    private readonly byte _argumentOffset;

    internal ClientCacheCommandKey(string operation)
        => _operation = operation;

    internal ClientCacheCommandKey(string operation, RespireValue a1)
        : this(operation) => (_a1, _prefixCount) = (a1, 1);

    internal ClientCacheCommandKey(string operation, RespireValue a1, RespireValue a2)
        : this(operation, a1) => (_a2, _prefixCount) = (a2, 2);

    internal ClientCacheCommandKey(string operation, RespireValue a1, RespireValue a2, RespireValue a3)
        : this(operation, a1, a2) => (_a3, _prefixCount) = (a3, 3);

    internal ClientCacheCommandKey(
        string operation, RespireValue a1, RespireValue a2, RespireValue a3, RespireValue a4)
        : this(operation, a1, a2, a3) => (_a4, _prefixCount) = (a4, 4);

    internal ClientCacheCommandKey(
        string operation,
        RespireValue a1,
        RespireValue a2,
        RespireValue a3,
        RespireValue a4,
        RespireValue a5)
        : this(operation, a1, a2, a3, a4) => (_a5, _prefixCount) = (a5, 5);

    internal ClientCacheCommandKey(string operation, RespireValue[] arguments)
        : this(operation) => _rest = arguments;

    internal ClientCacheCommandKey(string operation, RespireValue[] tokens, int argumentOffset)
        : this(operation, tokens) => _argumentOffset = checked((byte)argumentOffset);

    internal ClientCacheCommandKey(string operation, RespireValue a1, RespireValue[] rest)
        : this(operation, a1) => _rest = rest;

    internal ClientCacheCommandKey(string operation, RespireValue a1, RespireValue a2, RespireValue[] rest)
        : this(operation, a1, a2) => _rest = rest;

    internal int Count => _prefixCount + (_rest?.Length ?? 0);

    internal int ArgumentCount => Count - _argumentOffset;

    internal string Operation => _operation;

    internal RespireValue GetArgument(int index) => this[index + _argumentOffset];

    internal RespireValue this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return index switch
            {
                0 when _prefixCount > 0 => _a1,
                1 when _prefixCount > 1 => _a2,
                2 when _prefixCount > 2 => _a3,
                3 when _prefixCount > 3 => _a4,
                4 when _prefixCount > 4 => _a5,
                _ => _rest![index - _prefixCount],
            };
        }
    }

    internal ClientCacheCommandKey Snapshot()
    {
        // Small queries already have inline storage. Own every binary argument without
        // allocating a second container for immutable scalar/string arguments.
        switch (ArgumentCount)
        {
            case 0: return new(_operation);
            case 1: return new(_operation, GetArgument(0).Snapshot());
            case 2: return new(_operation, GetArgument(0).Snapshot(), GetArgument(1).Snapshot());
            case 3: return new(_operation, GetArgument(0).Snapshot(), GetArgument(1).Snapshot(), GetArgument(2).Snapshot());
            case 4: return new(_operation, GetArgument(0).Snapshot(), GetArgument(1).Snapshot(), GetArgument(2).Snapshot(),
                GetArgument(3).Snapshot());
            case 5: return new(_operation, GetArgument(0).Snapshot(), GetArgument(1).Snapshot(), GetArgument(2).Snapshot(),
                GetArgument(3).Snapshot(), GetArgument(4).Snapshot());
        }

        var arguments = new RespireValue[ArgumentCount];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = GetArgument(index).Snapshot();
        }

        return new ClientCacheCommandKey(_operation, arguments);
    }

    internal long OwnedSize
    {
        get
        {
            long size = 32 + _operation.Length * sizeof(char);
            for (var index = 0; index < ArgumentCount; index++)
            {
                size += GetArgument(index).GetWireLength();
            }

            return size;
        }
    }

    public bool Equals(ClientCacheCommandKey other)
    {
        if (!string.Equals(_operation, other._operation, StringComparison.Ordinal)
            || ArgumentCount != other.ArgumentCount)
        {
            return false;
        }

        for (var index = 0; index < ArgumentCount; index++)
        {
            if (GetArgument(index) != other.GetArgument(index))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ClientCacheCommandKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_operation, StringComparer.Ordinal);
        hash.Add(ArgumentCount);
        for (var index = 0; index < ArgumentCount; index++)
        {
            hash.Add(GetArgument(index));
        }

        return hash.ToHashCode();
    }
}
