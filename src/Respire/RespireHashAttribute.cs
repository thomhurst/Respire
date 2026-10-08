namespace Respire;

/// <summary>Generates a reflection-free hash codec named <c>ModelNameHashMapper</c>.</summary>
/// <param name="keyTemplate">A key template with property placeholders, for example <c>user:{Id}</c>.</param>
/// <remarks>
/// Apply to a public or internal top-level, non-generic partial class or record class.
/// Escape literal braces as <c>{{</c> and <c>}}</c>. The companion also provides explicit Redis
/// full writes, full reads and partial reads without reflection or dynamic serialization.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RespireHashAttribute(string keyTemplate) : Attribute
{
    /// <summary>Gets the model's key template.</summary>
    public string KeyTemplate { get; } = keyTemplate ?? throw new ArgumentNullException(nameof(keyTemplate));
}
