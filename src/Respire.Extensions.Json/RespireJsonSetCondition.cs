namespace Respire.Extensions.Json;

/// <summary>Conditional JSON.SET behavior.</summary>
public enum RespireJsonSetCondition
{
    /// <summary>Set the value unconditionally.</summary>
    None,
    /// <summary>Set only when the path does not exist.</summary>
    Nx,
    /// <summary>Set only when the path already exists.</summary>
    Xx,
}
