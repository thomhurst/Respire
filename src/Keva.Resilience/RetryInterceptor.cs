using Keva.Core.Pipeline;
using Keva.Core.Protocol;
using Polly;
using Polly.Retry;

namespace Keva.Resilience;

public class RetryInterceptor : IKevaInterceptor
{
    private readonly RetryOptions _options;
    private readonly AsyncRetryPolicy<RespValue> _retryPolicy;

    public RetryInterceptor(RetryOptions? options = null)
    {
        _options = options ?? new RetryOptions();
        _retryPolicy = BuildRetryPolicy();
    }

    public async ValueTask<RespValue> InterceptAsync(
        KevaInterceptorContext context,
        InterceptorDelegate next,
        CancellationToken cancellationToken = default)
    {
        // Check if retry should be applied
        if (!ShouldRetry(context))
        {
            return await next(context, cancellationToken);
        }

        // Execute with retry policy
        return await _retryPolicy.ExecuteAsync(async (ct) =>
        {
            var result = await next(context, ct);
            
            // Check if result indicates an error that should trigger retry
            if (result.IsError && ShouldRetryOnError(result))
            {
                throw new KevaRetryableException(result.GetErrorMessage());
            }
            
            return result;
        }, cancellationToken);
    }

    private AsyncRetryPolicy<RespValue> BuildRetryPolicy()
    {
        var policyBuilder = Policy<RespValue>
            .Handle<KevaRetryableException>()
            .Or<TimeoutException>()
            .Or<IOException>()
            .Or<SocketException>();

        // Add custom exception predicates
        if (_options.RetryableExceptions != null)
        {
            foreach (var exceptionType in _options.RetryableExceptions)
            {
                // Create a dynamic predicate for the exception type
                if (exceptionType == typeof(Exception))
                {
                    policyBuilder = policyBuilder.Or<Exception>();
                }
                else if (typeof(Exception).IsAssignableFrom(exceptionType))
                {
                    // Use reflection to call Or<T> with the specific type
                    var method = typeof(PolicyBuilder<RespValue>)
                        .GetMethod("Or", Type.EmptyTypes)
                        ?.MakeGenericMethod(exceptionType);
                    
                    if (method != null)
                    {
                        policyBuilder = (PolicyBuilder<RespValue>)method.Invoke(policyBuilder, null);
                    }
                }
            }
        }

        // Configure retry strategy
        return _options.Strategy switch
        {
            RetryStrategy.Fixed => policyBuilder.WaitAndRetryAsync(
                _options.MaxRetries,
                retryAttempt => _options.Delay,
                onRetry: OnRetryHandler),

            RetryStrategy.Linear => policyBuilder.WaitAndRetryAsync(
                _options.MaxRetries,
                retryAttempt => TimeSpan.FromMilliseconds(_options.Delay.TotalMilliseconds * retryAttempt),
                onRetry: OnRetryHandler),

            RetryStrategy.Exponential => policyBuilder.WaitAndRetryAsync(
                _options.MaxRetries,
                retryAttempt => TimeSpan.FromMilliseconds(
                    Math.Min(
                        _options.Delay.TotalMilliseconds * Math.Pow(2, retryAttempt - 1),
                        _options.MaxDelay.TotalMilliseconds)),
                onRetry: OnRetryHandler),

            RetryStrategy.ExponentialWithJitter => policyBuilder.WaitAndRetryAsync(
                _options.MaxRetries,
                retryAttempt =>
                {
                    var exponentialDelay = _options.Delay.TotalMilliseconds * Math.Pow(2, retryAttempt - 1);
                    var jitteredDelay = exponentialDelay * (0.7 + Random.Shared.NextDouble() * 0.3);
                    return TimeSpan.FromMilliseconds(Math.Min(jitteredDelay, _options.MaxDelay.TotalMilliseconds));
                },
                onRetry: OnRetryHandler),

            _ => throw new NotSupportedException($"Retry strategy {_options.Strategy} is not supported")
        };
    }

    private void OnRetryHandler(DelegateResult<RespValue> outcome, TimeSpan delay, int retryCount, Context context)
    {
        _options.OnRetry?.Invoke(new RetryContext
        {
            RetryCount = retryCount,
            Delay = delay,
            Exception = outcome.Exception,
            Result = outcome.Result
        });
    }

    private bool ShouldRetry(KevaInterceptorContext context)
    {
        // Check if already retrying
        if (context.Items.ContainsKey("RetryCount"))
        {
            var currentRetries = (int)context.Items["RetryCount"];
            if (currentRetries >= _options.MaxRetries)
            {
                return false;
            }
        }

        // Check if command is idempotent (safe to retry)
        if (_options.OnlyRetryIdempotent)
        {
            var commandName = context.Command.GetCommandName();
            if (!RespCommandParser.IsIdempotentCommand(commandName))
            {
                return false;
            }
        }

        return _options.Enabled;
    }

    private bool ShouldRetryOnError(RespValue error)
    {
        if (!error.IsError)
        {
            return false;
        }

        var errorMessage = error.GetErrorMessage();
        
        // Check for retryable error patterns
        if (_options.RetryableErrorPatterns != null)
        {
            foreach (var pattern in _options.RetryableErrorPatterns)
            {
                if (errorMessage.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Default retryable errors
        if (errorMessage.Contains("LOADING", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("MASTERDOWN", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("READONLY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

}

public class RetryOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxRetries { get; set; } = 3;
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);
    public RetryStrategy Strategy { get; set; } = RetryStrategy.ExponentialWithJitter;
    public bool OnlyRetryIdempotent { get; set; } = true;
    public List<Type>? RetryableExceptions { get; set; }
    public List<string>? RetryableErrorPatterns { get; set; }
    public Action<RetryContext>? OnRetry { get; set; }
}

public enum RetryStrategy
{
    Fixed,
    Linear,
    Exponential,
    ExponentialWithJitter
}

public class RetryContext
{
    public int RetryCount { get; set; }
    public TimeSpan Delay { get; set; }
    public Exception? Exception { get; set; }
    public RespValue Result { get; set; }
}

public class KevaRetryableException : Exception
{
    public KevaRetryableException(string message) : base(message) { }
    public KevaRetryableException(string message, Exception innerException) : base(message, innerException) { }
}

// Additional socket exception for retry scenarios
public class SocketException : IOException
{
    public SocketException(string message) : base(message) { }
    public SocketException(string message, Exception innerException) : base(message, innerException) { }
}