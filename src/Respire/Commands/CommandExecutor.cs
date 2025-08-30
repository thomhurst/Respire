using System.Runtime.CompilerServices;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>
/// Executes commands without delegate allocations using a switch expression
/// </summary>
public static class CommandExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask ExecuteAsync(ref CommandData command, PipelineCommandWriter writer, CancellationToken cancellationToken)
    {
        return command.Type switch
        {
            // String commands
            CommandType.Get => writer.WriteGetAsync(command.Arg1!, cancellationToken),
            CommandType.Set => writer.WriteSetAsync(command.Arg1!, command.Arg2!, cancellationToken),
            CommandType.Del => writer.WriteDelAsync(command.Arg1!, cancellationToken),
            CommandType.Exists => writer.WriteExistsAsync(command.Arg1!, cancellationToken),
            CommandType.Expire => writer.WriteExpireAsync(command.Arg1!, (int)command.IntValue, cancellationToken),
            CommandType.Ttl => writer.WriteTtlAsync(command.Arg1!, cancellationToken),
            CommandType.Incr => writer.WriteIncrAsync(command.Arg1!, cancellationToken),
            // TODO: Add these methods to PipelineCommandWriter
            // CommandType.Decr => writer.WriteDecrAsync(command.String1.Arg1, cancellationToken),
            // CommandType.IncrBy => writer.WriteIncrByAsync(command.Integer.Key, command.Integer.Value, cancellationToken),
            // CommandType.DecrBy => writer.WriteDecrByAsync(command.Integer.Key, command.Integer.Value, cancellationToken),
            // CommandType.Append => writer.WriteAppendAsync(command.String2.Arg1, command.String2.Arg2, cancellationToken),
            // CommandType.StrLen => writer.WriteStrLenAsync(command.String1.Arg1, cancellationToken),
            
            // Hash commands
            CommandType.HGet => writer.WriteHGetAsync(command.Arg1!, command.Arg2!, cancellationToken),
            CommandType.HSet => writer.WriteHSetAsync(command.Arg1!, command.Arg2!, command.Arg3!, cancellationToken),
            // TODO: Add these methods to PipelineCommandWriter
            // CommandType.HDel => writer.WriteHDelAsync(command.String2.Arg1, command.String2.Arg2, cancellationToken),
            // CommandType.HExists => writer.WriteHExistsAsync(command.String2.Arg1, command.String2.Arg2, cancellationToken),
            // CommandType.HLen => writer.WriteHLenAsync(command.String1.Arg1, cancellationToken),
            
            // List commands
            CommandType.LPush => writer.WriteLPushAsync(command.Arg1!, command.Arg2!, cancellationToken),
            // TODO: Add these methods to PipelineCommandWriter
            // CommandType.RPush => writer.WriteRPushAsync(command.String2.Arg1, command.String2.Arg2, cancellationToken),
            // CommandType.LPop => writer.WriteLPopAsync(command.String1.Arg1, cancellationToken),
            CommandType.RPop => writer.WriteRPopAsync(command.Arg1!, cancellationToken),
            // CommandType.LLen => writer.WriteLLenAsync(command.String1.Arg1, cancellationToken),
            
            // Set commands
            CommandType.SAdd => writer.WriteSAddAsync(command.Arg1!, command.Arg2!, cancellationToken),
            CommandType.SRem => writer.WriteSRemAsync(command.Arg1!, command.Arg2!, cancellationToken),
            // TODO: Add these methods to PipelineCommandWriter
            // CommandType.SMembers => writer.WriteSMembersAsync(command.String1.Arg1, cancellationToken),
            // CommandType.SIsMember => writer.WriteSIsMemberAsync(command.String2.Arg1, command.String2.Arg2, cancellationToken),
            // CommandType.SCard => writer.WriteSCardAsync(command.String1.Arg1, cancellationToken),
            
            // Connection commands
            CommandType.Ping => writer.WritePingAsync(cancellationToken),
            // TODO: Add these methods to PipelineCommandWriter
            // CommandType.Echo => writer.WriteEchoAsync(command.String1.Arg1, cancellationToken),
            
            // Server commands
            // TODO: Add these methods to PipelineCommandWriter
            // CommandType.FlushDb => writer.WriteFlushDbAsync(cancellationToken),
            // CommandType.FlushAll => writer.WriteFlushAllAsync(cancellationToken),
            // CommandType.DbSize => writer.WriteDbSizeAsync(cancellationToken),
            
            _ => ValueTask.CompletedTask
        };
    }
    
    /// <summary>
    /// Determines if a command expects a response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ExpectsResponse(CommandType type)
    {
        return type switch
        {
            // Commands that return values
            CommandType.Get => true,
            CommandType.Exists => true,
            CommandType.Ttl => true,
            CommandType.Incr => true,
            CommandType.Decr => true,
            CommandType.IncrBy => true,
            CommandType.DecrBy => true,
            CommandType.Append => true,
            CommandType.StrLen => true,
            CommandType.HGet => true,
            CommandType.HExists => true,
            CommandType.HLen => true,
            CommandType.HGetAll => true,
            CommandType.HKeys => true,
            CommandType.HVals => true,
            CommandType.LPop => true,
            CommandType.RPop => true,
            CommandType.LLen => true,
            CommandType.LRange => true,
            CommandType.SMembers => true,
            CommandType.SIsMember => true,
            CommandType.SCard => true,
            CommandType.ZRange => true,
            CommandType.ZRevRange => true,
            CommandType.ZRank => true,
            CommandType.ZRevRank => true,
            CommandType.ZScore => true,
            CommandType.ZCard => true,
            CommandType.Ping => true,
            CommandType.Echo => true,
            CommandType.DbSize => true,
            CommandType.Info => true,
            CommandType.Keys => true,
            CommandType.Scan => true,
            CommandType.Type => true,
            CommandType.RandomKey => true,
            
            // Commands that return status/counts
            CommandType.Set => true,
            CommandType.Del => true,
            CommandType.Expire => true,
            CommandType.HSet => true,
            CommandType.HDel => true,
            CommandType.LPush => true,
            CommandType.RPush => true,
            CommandType.LRem => true,
            CommandType.SAdd => true,
            CommandType.SRem => true,
            CommandType.ZAdd => true,
            CommandType.ZRem => true,
            
            _ => false
        };
    }
}