namespace Respire;

/// <summary>Declares the command name sent by a generated interface method.</summary>
/// <remarks>
/// Parameters are sent in declaration order, except cancellation tokens and command flags.
/// Arrays expand into separate arguments; byte arrays remain one binary argument.
/// Known catalog subcommands may be included in the name and use their catalog cache metadata;
/// other subcommands and options are ordinary arguments. Commands use raw execution policies,
/// including rejecting key-prefixed views. Unknown key layouts remain server-validated.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RespireCommandAttribute(string name) : Attribute
{
    /// <summary>The command name, such as <c>JSON.GET</c> or <c>FT.CURSOR READ</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Declares how this command affects keys tracked by client-side caching.</summary>
    /// <remarks>Unknown commands invalidate the full local cache.</remarks>
    public RespireCacheMutation Mutation { get; set; }
}
