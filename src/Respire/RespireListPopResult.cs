namespace Respire;

/// <summary>A selected list key and its popped value. Key storage is owned and excludes the client view's prefix.</summary>
/// <param name="Key">The selected key, suitable for reuse through the same client view.</param>
/// <param name="Value">The popped value, decoded as UTF-8.</param>
public readonly record struct RespireListPopResult(RespireKey Key, string Value);

/// <summary>A selected list key and its popped values in pop order. All returned storage is owned.</summary>
/// <param name="Key">The selected key without the client view's prefix.</param>
/// <param name="Values">The popped values, decoded as UTF-8, starting at the requested side.</param>
public readonly record struct RespireListPopManyResult(RespireKey Key, string[] Values);
