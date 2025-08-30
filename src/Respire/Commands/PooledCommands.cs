using System.Runtime.CompilerServices;
using Respire.Protocol;

namespace Respire.Commands;

/// <summary>
/// Poolable, zero-allocation command implementations that avoid lambda closures
/// </summary>
public static class PooledCommands
{
    /// <summary>
    /// Poolable SET command that avoids lambda allocations
    /// </summary>
    public struct SetCommand
    {
        private string _key;
        private string _value;
        private CancellationToken _cancellationToken;
        
        public SetCommand(string key, string value, CancellationToken cancellationToken = default)
        {
            _key = key;
            _value = value;
            _cancellationToken = cancellationToken;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask ExecuteAsync(PipelineCommandWriter writer)
        {
            return writer.WriteSetAsync(_key, _value, _cancellationToken);
        }
    }
    
    /// <summary>
    /// Poolable GET command that avoids lambda allocations
    /// </summary>
    public struct GetCommand
    {
        private string _key;
        private CancellationToken _cancellationToken;
        
        public GetCommand(string key, CancellationToken cancellationToken = default)
        {
            _key = key;
            _cancellationToken = cancellationToken;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask ExecuteAsync(PipelineCommandWriter writer)
        {
            return writer.WriteGetAsync(_key, _cancellationToken);
        }
    }
    
    /// <summary>
    /// Poolable DEL command that avoids lambda allocations
    /// </summary>
    public struct DelCommand
    {
        private string _key;
        private CancellationToken _cancellationToken;
        
        public DelCommand(string key, CancellationToken cancellationToken = default)
        {
            _key = key;
            _cancellationToken = cancellationToken;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask ExecuteAsync(PipelineCommandWriter writer)
        {
            return writer.WriteDelAsync(_key, _cancellationToken);
        }
    }
    
    /// <summary>
    /// Poolable EXISTS command that avoids lambda allocations
    /// </summary>
    public struct ExistsCommand
    {
        private string _key;
        private CancellationToken _cancellationToken;
        
        public ExistsCommand(string key, CancellationToken cancellationToken = default)
        {
            _key = key;
            _cancellationToken = cancellationToken;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask ExecuteAsync(PipelineCommandWriter writer)
        {
            return writer.WriteExistsAsync(_key, _cancellationToken);
        }
    }
    
    /// <summary>
    /// Poolable PING command that avoids lambda allocations
    /// </summary>
    public struct PingCommand
    {
        private CancellationToken _cancellationToken;
        
        public PingCommand(CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask ExecuteAsync(PipelineCommandWriter writer)
        {
            return writer.WritePingAsync(_cancellationToken);
        }
    }
    
    /// <summary>
    /// Poolable INCR command that avoids lambda allocations
    /// </summary>
    public struct IncrCommand
    {
        private string _key;
        private CancellationToken _cancellationToken;
        
        public IncrCommand(string key, CancellationToken cancellationToken = default)
        {
            _key = key;
            _cancellationToken = cancellationToken;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask ExecuteAsync(PipelineCommandWriter writer)
        {
            return writer.WriteIncrAsync(_key, _cancellationToken);
        }
    }
}