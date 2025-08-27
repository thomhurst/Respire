using Keva.Core.Pipeline;
using Keva.Core.Protocol;
using Polly;
using Polly.CircuitBreaker;

namespace Keva.Resilience;

public class CircuitBreakerInterceptor : IKevaInterceptor
{
    private readonly CircuitBreakerOptions _options;
    private readonly IAsyncPolicy<RespValue> _circuitBreakerPolicy;

    public CircuitBreakerInterceptor(CircuitBreakerOptions? options = null)
    {
        _options = options ?? new CircuitBreakerOptions();
        _circuitBreakerPolicy = BuildCircuitBreakerPolicy();
    }

    public async ValueTask<RespValue> InterceptAsync(
        KevaInterceptorContext context,
        InterceptorDelegate next,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return await next(context, cancellationToken);
        }

        try
        {
            return await _circuitBreakerPolicy.ExecuteAsync(async (ct) =>
            {
                var result = await next(context, ct);
                
                // Check if result indicates a failure that should be counted
                if (result.IsError && ShouldCountAsFailure(result))
                {
                    throw new KevaCircuitBreakerException(result.GetErrorMessage());
                }
                
                return result;
            }, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            // Check if it's an isolated circuit exception (subclass)
            if (ex is IsolatedCircuitException)
            {
                // Circuit is isolated (manually opened)
                return RespValue.Error($"CIRCUITISOLATED Circuit breaker is isolated: {ex.Message}");
            }
            
            // Circuit is open, return error immediately
            return RespValue.Error($"CIRCUITOPEN Circuit breaker is open: {ex.Message}");
        }
    }

    private IAsyncPolicy<RespValue> BuildCircuitBreakerPolicy()
    {
        var policyBuilder = Policy<RespValue>
            .Handle<KevaCircuitBreakerException>()
            .Or<TimeoutException>()
            .Or<IOException>()
            .Or<SocketException>();

        // Add custom exception predicates
        if (_options.FailureExceptions != null)
        {
            foreach (var exceptionType in _options.FailureExceptions)
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

        // Configure circuit breaker based on strategy
        return _options.Strategy switch
        {
            CircuitBreakerStrategy.Basic => policyBuilder.CircuitBreakerAsync(
                _options.FailureThreshold,
                _options.BreakDuration,
                OnBreak,
                OnReset,
                OnHalfOpen),

            CircuitBreakerStrategy.Advanced => policyBuilder.AdvancedCircuitBreakerAsync(
                _options.FailureRatio,
                _options.SamplingDuration,
                _options.MinimumThroughput,
                _options.BreakDuration,
                OnBreak,
                OnReset,
                OnHalfOpen),

            _ => throw new NotSupportedException($"Circuit breaker strategy {_options.Strategy} is not supported")
        };
    }

    private void OnBreak(DelegateResult<RespValue> result, TimeSpan duration, Context context)
    {
        var state = new CircuitBreakerState
        {
            State = CircuitState.Open,
            Duration = duration,
            Exception = result.Exception,
            Result = result.Result,
            Timestamp = DateTime.UtcNow
        };

        _options.OnBreak?.Invoke(state);
    }

    private void OnReset(Context context)
    {
        var state = new CircuitBreakerState
        {
            State = CircuitState.Closed,
            Timestamp = DateTime.UtcNow
        };

        _options.OnReset?.Invoke(state);
    }

    private void OnHalfOpen()
    {
        var state = new CircuitBreakerState
        {
            State = CircuitState.HalfOpen,
            Timestamp = DateTime.UtcNow
        };

        _options.OnHalfOpen?.Invoke(state);
    }

    private bool ShouldCountAsFailure(RespValue error)
    {
        if (!error.IsError)
        {
            return false;
        }

        var errorMessage = error.GetErrorMessage();
        
        // Check for failure patterns
        if (_options.FailurePatterns != null)
        {
            foreach (var pattern in _options.FailurePatterns)
            {
                if (errorMessage.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Don't count client errors as failures
        if (errorMessage.StartsWith("ERR", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.StartsWith("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Count infrastructure errors as failures
        if (errorMessage.Contains("OOM", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("BUSY", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("MISCONF", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _options.CountAllErrorsAsFailures;
    }
}

public class CircuitBreakerOptions
{
    public bool Enabled { get; set; } = true;
    public CircuitBreakerStrategy Strategy { get; set; } = CircuitBreakerStrategy.Advanced;
    
    // Basic strategy options
    public int FailureThreshold { get; set; } = 5;
    
    // Advanced strategy options
    public double FailureRatio { get; set; } = 0.5; // 50% failure rate
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public int MinimumThroughput { get; set; } = 10;
    
    // Common options
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
    public bool CountAllErrorsAsFailures { get; set; } = false;
    public List<Type>? FailureExceptions { get; set; }
    public List<string>? FailurePatterns { get; set; }
    
    // Event handlers
    public Action<CircuitBreakerState>? OnBreak { get; set; }
    public Action<CircuitBreakerState>? OnReset { get; set; }
    public Action<CircuitBreakerState>? OnHalfOpen { get; set; }
}

public enum CircuitBreakerStrategy
{
    Basic,
    Advanced
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public class CircuitBreakerState
{
    public CircuitState State { get; set; }
    public TimeSpan? Duration { get; set; }
    public Exception? Exception { get; set; }
    public RespValue Result { get; set; }
    public DateTime Timestamp { get; set; }
}

public class KevaCircuitBreakerException : Exception
{
    public KevaCircuitBreakerException(string message) : base(message) { }
    public KevaCircuitBreakerException(string message, Exception innerException) : base(message, innerException) { }
}