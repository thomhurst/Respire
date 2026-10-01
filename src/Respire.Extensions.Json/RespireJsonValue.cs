namespace Respire.Extensions.Json;

/// <summary>Separates a missing key/path from a JSON document whose value is null.</summary>
public readonly record struct RespireJsonValue<T>(bool Found, T? Value);

/// <summary>A typed JSON value to include in JSON.MSET.</summary>
public readonly record struct RespireJsonSetEntry<T>(RespireKey Key, T Value, RespireJsonPath Path = default);
