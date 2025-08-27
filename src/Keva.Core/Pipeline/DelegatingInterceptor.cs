using Keva.Core.Protocol;

namespace Keva.Core.Pipeline;

public abstract class DelegatingInterceptor : IKevaInterceptor
{
    protected virtual ValueTask<RespValue> OnRequestAsync(
        CommandInfo commandInfo,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<RespValue>(default);
    }
    
    protected virtual ValueTask<RespValue> OnResponseAsync(
        CommandInfo commandInfo,
        RespValue response,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(response);
    }
    
    protected virtual ValueTask<RespValue> OnErrorAsync(
        CommandInfo commandInfo,
        Exception exception,
        CancellationToken cancellationToken)
    {
        throw exception;
    }
    
    public async ValueTask<RespValue> InterceptAsync(
        CommandInfo commandInfo,
        InterceptorDelegate next,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Pre-processing
            var earlyResponse = await OnRequestAsync(commandInfo, cancellationToken).ConfigureAwait(false);
            if (earlyResponse.Type != RespDataType.None)
            {
                return earlyResponse;
            }
            
            // Call next interceptor
            var response = await next(commandInfo, cancellationToken).ConfigureAwait(false);
            
            // Post-processing
            return await OnResponseAsync(commandInfo, response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await OnErrorAsync(commandInfo, ex, cancellationToken).ConfigureAwait(false);
        }
    }
}