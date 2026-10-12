using Respire.Protocol;

namespace Respire.Internal;

/// <summary>
/// Stateless reply conversion for the hottest scalar shapes (integer, flag, OK). Used as a
/// value-type generic argument, it makes the JIT specialize the whole send path and call
/// <see cref="Convert"/> directly instead of through a shared-generic delegate.
/// </summary>
internal interface IScalarResponseConverter<TResult>
{
    static abstract TResult Convert(in RespValue response);
}

internal readonly struct IntegerResponseConverter : IScalarResponseConverter<long>
{
    public static long Convert(in RespValue response) => ResponseReader.Integer(in response);
}

internal readonly struct FlagResponseConverter : IScalarResponseConverter<bool>
{
    public static bool Convert(in RespValue response) => ResponseReader.Flag(in response);
}

internal readonly struct OkOrNullResponseConverter : IScalarResponseConverter<bool>
{
    public static bool Convert(in RespValue response) => ResponseReader.OkOrNull(in response);
}

internal readonly struct OkResponseConverter : IScalarResponseConverter<bool>
{
    public static bool Convert(in RespValue response) => ResponseReader.Ok(in response);
}

/// <summary>
/// Adapts a scalar converter to the delegate-based routes (replica, cluster, retries,
/// capacity waits). The converter itself is the state, so those routes stay specialized too.
/// </summary>
internal static class ScalarResponseConverter<TConverter, TResult>
    where TConverter : struct, IScalarResponseConverter<TResult>
{
    internal static readonly ResponseConverter<TConverter, TResult> Delegate =
        static (TConverter _, in RespValue response) => TConverter.Convert(in response);
}
