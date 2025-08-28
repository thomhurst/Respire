using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// A reusable, poolable ValueTask completion source to eliminate Task allocations
/// </summary>
internal sealed class ValueTaskCompletionSource : IValueTaskSource<RespireValue>, IValueTaskSource
{
    private ManualResetValueTaskSourceCore<RespireValue> _core;
    private short _version;
    
    public short Version => _version;
    
    public ValueTaskCompletionSource()
    {
        _core = new ManualResetValueTaskSourceCore<RespireValue>();
    }
    
    public void Reset()
    {
        _core.Reset();
        unchecked { _version++; }
    }
    
    public void SetResult(RespireValue result)
    {
        _core.SetResult(result);
    }
    
    public void SetException(Exception exception)
    {
        _core.SetException(exception);
    }
    
    public bool TrySetResult(RespireValue result)
    {
        try
        {
            _core.SetResult(result);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public bool TrySetException(Exception exception)
    {
        try
        {
            _core.SetException(exception);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public ValueTask<RespireValue> GetValueTask()
    {
        return new ValueTask<RespireValue>(this, _version);
    }
    
    public RespireValue GetResult(short token)
    {
        return _core.GetResult(token);
    }
    
    public ValueTaskSourceStatus GetStatus(short token)
    {
        return _core.GetStatus(token);
    }
    
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
    }
    
    void IValueTaskSource.GetResult(short token) => _core.GetResult(token);
}