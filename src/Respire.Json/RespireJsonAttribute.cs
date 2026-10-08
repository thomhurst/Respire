namespace Respire.Json;

/// <summary>Generates a reflection-free JSON codec named <c>ModelNameJsonMapper</c>.</summary>
/// <param name="keyTemplate">A key template with scalar property placeholders, for example <c>user:{Id}</c>.</param>
/// <remarks>Apply to a top-level, non-generic partial class or record class. Escape literal braces as <c>{{</c> and <c>}}</c>.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RespireJsonAttribute(string keyTemplate) : Attribute
{
    /// <summary>The model key template.</summary>
    public string KeyTemplate { get; } = keyTemplate;
}
