using Microsoft.Extensions.ObjectPool;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// Object pool policy for PipelineCommandWriter instances
/// </summary>
internal sealed class PipelineCommandWriterPooledObjectPolicy : IPooledObjectPolicy<PipelineCommandWriter>
{
    private readonly PipelineConnection _connection;
    
    public PipelineCommandWriterPooledObjectPolicy(PipelineConnection connection)
    {
        _connection = connection;
    }
    
    public PipelineCommandWriter Create()
    {
        return new PipelineCommandWriter(_connection);
    }
    
    public bool Return(PipelineCommandWriter obj)
    {
        if (obj == null)
            return false;
            
        // Reset the writer for reuse
        obj.Reset();
        obj.UpdateConnection(_connection);
        return true;
    }
}