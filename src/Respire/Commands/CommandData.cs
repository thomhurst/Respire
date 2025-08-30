using System.Runtime.CompilerServices;

namespace Respire.Commands;

/// <summary>
/// Zero-allocation command data structure.
/// Uses a simple struct without explicit layout to avoid type conflicts.
/// </summary>
public readonly struct CommandData
{
    public readonly CommandType Type;
    public readonly string? Arg1;
    public readonly string? Arg2;
    public readonly string? Arg3;
    public readonly long IntValue;
    public readonly double DoubleValue;
    
    // Constructors for different command types
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandData(CommandType type, string arg1)
    {
        Type = type;
        Arg1 = arg1;
        Arg2 = null;
        Arg3 = null;
        IntValue = 0;
        DoubleValue = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandData(CommandType type, string arg1, string arg2)
    {
        Type = type;
        Arg1 = arg1;
        Arg2 = arg2;
        Arg3 = null;
        IntValue = 0;
        DoubleValue = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandData(CommandType type, string arg1, string arg2, string arg3)
    {
        Type = type;
        Arg1 = arg1;
        Arg2 = arg2;
        Arg3 = arg3;
        IntValue = 0;
        DoubleValue = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandData(CommandType type, string arg1, long value)
    {
        Type = type;
        Arg1 = arg1;
        Arg2 = null;
        Arg3 = null;
        IntValue = value;
        DoubleValue = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandData(CommandType type, string arg1, double value)
    {
        Type = type;
        Arg1 = arg1;
        Arg2 = null;
        Arg3 = null;
        IntValue = 0;
        DoubleValue = value;
    }
}