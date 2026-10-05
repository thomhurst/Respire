namespace Respire;

/// <summary>Conditional ZADD flags. NX conflicts with XX, GT, and LT; GT conflicts with LT.</summary>
/// <remarks>INCR is exposed only by the single-member Increment overloads, which return a nullable score.</remarks>
[Flags]
public enum RespireSortedSetAddOptions
{
    /// <summary>Add new members and update existing scores; count only new members.</summary>
    None = 0,
    /// <summary>Only add members that do not exist (NX).</summary>
    Nx = 1,
    /// <summary>Only update members that already exist (XX).</summary>
    Xx = 2,
    /// <summary>Only increase existing scores; new members are still added unless XX is set (GT, Redis 6.2+).</summary>
    Gt = 4,
    /// <summary>Only decrease existing scores; new members are still added unless XX is set (LT, Redis 6.2+).</summary>
    Lt = 8,
    /// <summary>Count changed scores as well as new members (CH). INCR still returns the score.</summary>
    Ch = 16,
}
