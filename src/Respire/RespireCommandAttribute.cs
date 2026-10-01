namespace Respire;

/// <summary>Declares the single command token sent by a generated interface method.</summary>
/// <remarks>
/// Parameters are sent in declaration order, except cancellation tokens and command flags.
/// Arrays expand into separate arguments; byte arrays remain one binary argument.
/// Subcommands and options are ordinary arguments. Commands use raw execution policies, including
/// rejecting key-prefixed views. Unknown key layouts remain server-validated.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RespireCommandAttribute(string name) : Attribute
{
    /// <summary>The command token, such as <c>JSON.GET</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Declares how this command affects keys tracked by client-side caching.</summary>
    /// <remarks>Unknown commands invalidate the full local cache.</remarks>
    public RespireCacheMutation Mutation { get; set; }
}
