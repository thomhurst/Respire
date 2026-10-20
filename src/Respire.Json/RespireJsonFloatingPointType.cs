namespace Respire.Json;

/// <summary>The storage type for JSON floating point homogeneous arrays (Redis 8.8 or later).</summary>
public enum RespireJsonFloatingPointType
{
    /// <summary>IEEE 754 half precision.</summary>
    Fp16,
    /// <summary>16-bit brain floating point.</summary>
    Bf16,
    /// <summary>IEEE 754 single precision.</summary>
    Fp32,
    /// <summary>IEEE 754 double precision.</summary>
    Fp64,
}
