using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Coordination;

namespace Respire.RateLimiting;

/// <summary>Registers Redis-backed policies with ASP.NET Core's rate limiting middleware.</summary>
public static class RespireRateLimiterOptionsExtensions
{
    /// <summary>Adds a named fixed-window policy sharing one Redis budget across application instances.</summary>
    /// <remarks>Algorithm settings are validated when the middleware first creates the limiter.
    /// The middleware owns the limiter; the application owns the Redis client. Rejections return 429
    /// with Retry-After when available, then invoke <see cref="RateLimiterOptions.OnRejected"/>.</remarks>
    public static RateLimiterOptions AddRespireFixedWindowLimiter(this RateLimiterOptions options,
        string policyName, RespireRateLimiters rateLimiters, RespireKey key, int permitLimit, TimeSpan window,
        int queueLimit = 0, QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
    {
        ValidateKey(rateLimiters, key);
        return options.AddRespirePartitionedLimiter(policyName, static _ => 0,
            _ => rateLimiters.FixedWindow(key, permitLimit, window, queueLimit, queueProcessingOrder));
    }

    /// <summary>Adds a named segmented sliding-window policy sharing one Redis budget across application instances.</summary>
    /// <remarks>Algorithm settings are validated on first use. Queue capacity is local to each application instance.
    /// Rejections return 429 with Retry-After when available, then invoke <see cref="RateLimiterOptions.OnRejected"/>.</remarks>
    public static RateLimiterOptions AddRespireSlidingWindowLimiter(this RateLimiterOptions options,
        string policyName, RespireRateLimiters rateLimiters, RespireKey key, int permitLimit, TimeSpan window,
        int segments, int queueLimit = 0, QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
    {
        ValidateKey(rateLimiters, key);
        return options.AddRespirePartitionedLimiter(policyName, static _ => 0,
            _ => rateLimiters.SlidingWindow(key, permitLimit, window, segments, queueLimit, queueProcessingOrder));
    }

    /// <summary>Adds a named token-bucket policy sharing one Redis budget across application instances.</summary>
    /// <remarks>Algorithm settings are validated on first use. Queue capacity is local to each application instance.
    /// Rejections return 429 with Retry-After when available, then invoke <see cref="RateLimiterOptions.OnRejected"/>.</remarks>
    public static RateLimiterOptions AddRespireTokenBucketLimiter(this RateLimiterOptions options,
        string policyName, RespireRateLimiters rateLimiters, RespireKey key, int tokenLimit, int tokensPerPeriod,
        TimeSpan replenishmentPeriod, int queueLimit = 0,
        QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
    {
        ValidateKey(rateLimiters, key);
        return options.AddRespirePartitionedLimiter(policyName, static _ => 0,
            _ => rateLimiters.TokenBucket(key, tokenLimit, tokensPerPeriod, replenishmentPeriod, queueLimit, queueProcessingOrder));
    }

    /// <summary>Adds a named policy with a local limiter cache keyed by user, IP, API-key identity, or another partition.</summary>
    /// <remarks>The selector must return a stable, non-null key throughout each request. The factory must create a new
    /// limiter on every call, including after idle eviction. Include the partition in its Redis key to isolate budgets.
    /// The middleware owns and disposes limiters, including idle ones; the application owns the Redis client.
    /// Rejections return 429 with Retry-After when available, then invoke <see cref="RateLimiterOptions.OnRejected"/>.</remarks>
    public static RateLimiterOptions AddRespirePartitionedLimiter<TPartitionKey>(this RateLimiterOptions options,
        string policyName, Func<HttpContext, TPartitionKey> partitionKeySelector, Func<TPartitionKey, RateLimiter> limiterFactory)
        where TPartitionKey : notnull
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        ArgumentNullException.ThrowIfNull(partitionKeySelector);
        ArgumentNullException.ThrowIfNull(limiterFactory);
        return options.AddPolicy(policyName, new RespirePolicy<TPartitionKey>(options, partitionKeySelector, limiterFactory));
    }

    private static void ValidateKey(RespireRateLimiters rateLimiters, RespireKey key)
    {
        ArgumentNullException.ThrowIfNull(rateLimiters);
        if (key.IsEmpty) throw new ArgumentException("The rate-limit key must not be empty.", nameof(key));
    }

    private sealed class RespirePolicy<TPartitionKey>(RateLimiterOptions options,
        Func<HttpContext, TPartitionKey> partitionKeySelector, Func<TPartitionKey, RateLimiter> limiterFactory)
        : IRateLimiterPolicy<TPartitionKey> where TPartitionKey : notnull
    {
        public RateLimitPartition<TPartitionKey> GetPartition(HttpContext httpContext)
        {
            var key = partitionKeySelector(httpContext);
            if (key is null) throw new InvalidOperationException("The partition selector must not return null.");
            return RateLimitPartition.Get(key, partitionKey =>
            {
                var lifetime = httpContext.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                return new HostOwnedRateLimiter(limiterFactory(partitionKey), lifetime.ApplicationStopped);
            });
        }

        public Func<OnRejectedContext, CancellationToken, ValueTask> OnRejected => RejectAsync;

        private async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                var seconds = Math.Max(1L, (long)Math.Ceiling(retryAfter.TotalSeconds));
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            }

            if (options.OnRejected is { } onRejected)
                await onRejected(context, cancellationToken).ConfigureAwait(false);
        }
    }

    // ASP.NET Core evicts idle partitions, but does not dispose its endpoint cache on host shutdown.
    // Dispose remaining limiters after requests drain, so queued acquisitions survive ApplicationStopping.
    // Unregister on idle disposal so the host token does not retain evicted partitions.
    private sealed class HostOwnedRateLimiter : RateLimiter
    {
        private readonly RateLimiter _inner;
        private readonly CancellationTokenRegistration _stopped;
        private int _disposed;

        public HostOwnedRateLimiter(RateLimiter inner, CancellationToken applicationStopped)
        {
            _inner = inner ?? throw new InvalidOperationException("The limiter factory must not return null.");
            _stopped = applicationStopped.UnsafeRegister(static state => ((HostOwnedRateLimiter)state!).Dispose(), this);
            // Registration can run synchronously if the host has already stopped.
            if (Volatile.Read(ref _disposed) != 0) _stopped.Unregister();
        }

        public override TimeSpan? IdleDuration => _inner.IdleDuration;
        public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => _inner.AttemptAcquire(permitCount);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
            => _inner.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _stopped.Unregister();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override async ValueTask DisposeAsyncCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _stopped.Unregister();
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
