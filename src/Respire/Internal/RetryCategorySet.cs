namespace Respire.Internal;

/// <summary>The distinct audited retry categories of every member in one complete unit.</summary>
/// <remarks>
/// A unit can be resent only when the policy permits each member category individually. The set
/// never orders categories for that decision. <see cref="Label"/> only names telemetry.
/// </remarks>
internal struct RetryCategorySet
{
    private ushort _mask;

    internal readonly bool IsEmpty => _mask == 0;

    internal readonly bool Contains(RespireCommandRetryCategory category) => (_mask & Bit(category)) != 0;

    internal void Add(RespireCommandRetryCategory category) => _mask |= Bit(category);

    /// <summary>The highest-risk member category, used only as the unit's telemetry label.</summary>
    internal readonly RespireCommandRetryCategory Label
    {
        get
        {
            if (Contains(RespireCommandRetryCategory.Never)) return RespireCommandRetryCategory.Never;
            for (var category = RespireCommandRetryCategory.ServerAdmin; category > RespireCommandRetryCategory.Never; category--)
            {
                if (Contains(category)) return category;
            }
            return RespireCommandRetryCategory.Never;
        }
    }

    // Undefined values share one bit with Never, so an unaudited member always blocks the unit.
    private static ushort Bit(RespireCommandRetryCategory category)
        => Enum.IsDefined(category) ? (ushort)(1 << (int)category) : (ushort)1;
}
