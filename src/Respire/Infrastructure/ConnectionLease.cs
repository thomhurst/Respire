using System.Runtime.CompilerServices;

namespace Respire.Infrastructure;

/// <summary>
/// Lightweight connection lease that must be explicitly returned to avoid boxing
/// Used for zero-allocation GET operations
/// </summary>
public readonly struct ConnectionLease
{
    private readonly RespireConnectionMultiplexer? _multiplexer;
    private readonly int _index;
    
    public PipelineConnection? Connection { get; }
    public bool IsValid => Connection != null;
    
    internal ConnectionLease(RespireConnectionMultiplexer multiplexer, int index, PipelineConnection connection)
    {
        _multiplexer = multiplexer;
        _index = index;
        Connection = connection;
    }
    
    /// <summary>
    /// Returns the connection to the pool. Must be called when done.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return()
    {
        _multiplexer?.ReleaseConnection(_index);
    }
}