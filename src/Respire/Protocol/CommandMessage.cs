using System.Buffers;
using System.Runtime.CompilerServices;

namespace Respire.Protocol;

/// <summary>
/// Lightweight command message struct for pre-built Redis commands
/// Avoids allocations by reusing static instances and pooled buffers
/// </summary>
public readonly struct CommandMessage
{
    private readonly ReadOnlyMemory<byte> _preBuiltCommand;
    private readonly string? _dynamicKey;
    private readonly ProtocolCommandType _commandType;
    
    public ReadOnlyMemory<byte> PreBuiltCommand => _preBuiltCommand;
    public string? DynamicKey => _dynamicKey;
    public ProtocolCommandType CommandType => _commandType;
    public bool IsPreBuilt => _preBuiltCommand.Length > 0;
    
    private CommandMessage(ReadOnlyMemory<byte> preBuiltCommand, ProtocolCommandType commandType)
    {
        _preBuiltCommand = preBuiltCommand;
        _dynamicKey = null;
        _commandType = commandType;
    }
    
    private CommandMessage(string dynamicKey, ProtocolCommandType commandType)
    {
        _preBuiltCommand = default;
        _dynamicKey = dynamicKey;
        _commandType = commandType;
    }
    
    /// <summary>
    /// Creates a message for a pre-built command (zero allocation)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CommandMessage CreatePreBuilt(ReadOnlyMemory<byte> command, ProtocolCommandType commandType)
    {
        return new CommandMessage(command, commandType);
    }
    
    /// <summary>
    /// Creates a message for a dynamic command that needs building
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CommandMessage CreateDynamic(string key, ProtocolCommandType commandType)
    {
        return new CommandMessage(key, commandType);
    }
    
    /// <summary>
    /// Builds the command into the provided buffer writer
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int BuildCommand(Span<byte> buffer)
    {
        if (IsPreBuilt)
        {
            _preBuiltCommand.Span.CopyTo(buffer);
            return _preBuiltCommand.Length;
        }
        
        return _commandType switch
        {
            ProtocolCommandType.Get => RespCommands.BuildGetCommand(buffer, _dynamicKey!),
            ProtocolCommandType.Set => throw new InvalidOperationException("SET command requires value parameter"),
            ProtocolCommandType.Del => RespCommands.BuildDelCommand(buffer, _dynamicKey!),
            ProtocolCommandType.Ping => RespCommands.BuildPingCommand(buffer),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}

/// <summary>
/// Redis command types for message building
/// </summary>
public enum ProtocolCommandType : byte
{
    Get = 0,
    Set = 1,
    Del = 2,
    Ping = 3,
    Exists = 4
}

/// <summary>
/// Static cache for common command messages to avoid repeated allocations
/// </summary>
public static class CommandMessages
{
    // Pre-built common commands
    private static readonly ReadOnlyMemory<byte> _pingCommand = RespCommands.BuildPingCommandBytes();
    
    /// <summary>
    /// Reusable PING command message (zero allocation)
    /// </summary>
    public static readonly CommandMessage Ping = CommandMessage.CreatePreBuilt(_pingCommand, ProtocolCommandType.Ping);
    
    /// <summary>
    /// Creates a GET command message, preferring pre-built if available
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CommandMessage Get(string key)
    {
        // For now, always create dynamic - could cache common keys later
        return CommandMessage.CreateDynamic(key, ProtocolCommandType.Get);
    }
    
    /// <summary>
    /// Creates a DEL command message
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CommandMessage Del(string key)
    {
        return CommandMessage.CreateDynamic(key, ProtocolCommandType.Del);
    }
}