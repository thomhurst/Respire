namespace Respire.Serialization;

internal static class SerializationWarnings
{
    internal const string UnreferencedCode =
        "The configured serializer may require members that cannot be discovered statically. "
        + "Use SystemTextJsonSerializer.FromContext with a JsonSerializerContext in trimmed applications.";

    internal const string DynamicCode =
        "The configured serializer may require runtime code generation. "
        + "Use SystemTextJsonSerializer.FromContext with a JsonSerializerContext in NativeAOT applications.";
}
