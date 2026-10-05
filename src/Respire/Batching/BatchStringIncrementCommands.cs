using Respire.Commands;

namespace Respire;

public partial interface IBatchStringCommands
{
    /// <summary>Queues INCREX BYINT with optional bounds and expiry, returning the value and applied delta. Redis 8.8+.</summary>
    RespirePending<RespireIncrementResult<long>> IncrementExtended(RespireKey key, long by = 1, IntegerIncrementOptions options = default);
    /// <summary>Queues INCREX BYFLOAT with optional bounds and expiry. Redis 8.8+. Replies use .NET double precision.</summary>
    RespirePending<RespireIncrementResult<double>> IncrementExtended(RespireKey key, double by, FloatIncrementOptions options = default);
}

internal sealed partial class BatchStringCommands
{
    public RespirePending<RespireIncrementResult<long>> IncrementExtended(RespireKey key, long by = 1, IntegerIncrementOptions options = default)
        => sink.Add<Cmd1N, RespireIncrementResult<long>>("INCREX",
            StringCommands.BuildIncrementExtended(sink.Client, sink.DefersSerialization ? key.Snapshot() : key, by, options),
            static (c, v) => StringCommands.ReadIntegerIncrement(in v));

    public RespirePending<RespireIncrementResult<double>> IncrementExtended(RespireKey key, double by, FloatIncrementOptions options = default)
        => sink.Add<Cmd1N, RespireIncrementResult<double>>("INCREX",
            StringCommands.BuildIncrementExtended(sink.Client, sink.DefersSerialization ? key.Snapshot() : key, by, options),
            static (c, v) => StringCommands.ReadFloatIncrement(in v));
}
