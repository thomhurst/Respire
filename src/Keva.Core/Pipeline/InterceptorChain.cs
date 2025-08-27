using Keva.Core.Protocol;

namespace Keva.Core.Pipeline;

public sealed class InterceptorChain
{
    private readonly IReadOnlyList<IKevaInterceptor> _interceptors;
    private readonly InterceptorDelegate _terminalHandler;
    
    public InterceptorChain(IEnumerable<IKevaInterceptor> interceptors, InterceptorDelegate terminalHandler)
    {
        _interceptors = interceptors.ToList();
        _terminalHandler = terminalHandler ?? throw new ArgumentNullException(nameof(terminalHandler));
    }
    
    public ValueTask<RespValue> ExecuteAsync(CommandInfo commandInfo, CancellationToken cancellationToken = default)
    {
        if (_interceptors.Count == 0)
        {
            return _terminalHandler(commandInfo, cancellationToken);
        }
        
        return ExecuteInterceptorChain(commandInfo, 0, cancellationToken);
    }
    
    private ValueTask<RespValue> ExecuteInterceptorChain(CommandInfo commandInfo, int index, CancellationToken cancellationToken)
    {
        if (index >= _interceptors.Count)
        {
            return _terminalHandler(commandInfo, cancellationToken);
        }
        
        var currentInterceptor = _interceptors[index];
        
        InterceptorDelegate next = (CommandInfo cmd, CancellationToken ct) => ExecuteInterceptorChain(cmd, index + 1, ct);
        
        return currentInterceptor.InterceptAsync(commandInfo, next, cancellationToken);
    }
    
    public static InterceptorChainBuilder CreateBuilder(InterceptorDelegate terminalHandler)
    {
        return new InterceptorChainBuilder(terminalHandler);
    }
}

public sealed class InterceptorChainBuilder
{
    private readonly List<IKevaInterceptor> _interceptors = new();
    private readonly InterceptorDelegate _terminalHandler;
    
    internal InterceptorChainBuilder(InterceptorDelegate terminalHandler)
    {
        _terminalHandler = terminalHandler;
    }
    
    public InterceptorChainBuilder Add(IKevaInterceptor interceptor)
    {
        if (interceptor == null) throw new ArgumentNullException(nameof(interceptor));
        _interceptors.Add(interceptor);
        return this;
    }
    
    public InterceptorChainBuilder Add<T>() where T : IKevaInterceptor, new()
    {
        _interceptors.Add(new T());
        return this;
    }
    
    public InterceptorChainBuilder AddRange(IEnumerable<IKevaInterceptor> interceptors)
    {
        _interceptors.AddRange(interceptors);
        return this;
    }
    
    public InterceptorChainBuilder Insert(int index, IKevaInterceptor interceptor)
    {
        _interceptors.Insert(index, interceptor);
        return this;
    }
    
    public InterceptorChain Build()
    {
        return new InterceptorChain(_interceptors, _terminalHandler);
    }
}