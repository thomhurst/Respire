namespace Respire;

/// <summary>
/// Generates a sealed implementation named after this interface with an <c>Implementation</c> suffix,
/// in the same namespace. Construct it with an <see cref="IRespireClient"/>.
/// </summary>
/// <remarks>Interfaces must be top-level, non-generic, and have no base interfaces.</remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class RespireCommandsAttribute : Attribute;
