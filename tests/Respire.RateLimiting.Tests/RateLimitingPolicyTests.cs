using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.RateLimiting.Tests;

public class RateLimitingPolicyTests
{
    [Test]
    [Arguments(1, "1")]
    [Arguments(1000, "1")]
    [Arguments(1001, "2")]
    [Arguments(0, "1")]
    public async Task RetryAfterRoundsUpToWholeSeconds(int milliseconds, string expected)
    {
        await using var limiter = new TestLimiter(TimeSpan.FromMilliseconds(milliseconds));
        await using var app = new RateLimitingApplication(options =>
            options.AddRespirePartitionedLimiter("api", _ => "shared", _ => limiter));
        using var http = app.CreateClient();
        using var response = await http.GetAsync("/api");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(response.Headers.GetValues("Retry-After").Single()).IsEqualTo(expected);
    }

    [Test]
    public async Task MissingMetadataDoesNotInventRetryAfterOrAffectUnmarkedEndpoints()
    {
        await using var limiter = new TestLimiter();
        await using var app = new RateLimitingApplication(options =>
            options.AddRespirePartitionedLimiter("api", _ => "shared", _ => limiter));
        using var http = app.CreateClient();
        using var denied = await http.GetAsync("/api");
        using var unlimited = await http.GetAsync("/unlimited");
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(denied.Headers.Contains("Retry-After")).IsFalse();
        await Assert.That(unlimited.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CustomRejectionCallbackRunsOnceAndCanOverrideResponse(bool configureBefore)
    {
        await using var limiter = new TestLimiter(TimeSpan.FromMilliseconds(1001));
        var calls = 0;
        var sawDefaults = false;
        ValueTask Reject(OnRejectedContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            sawDefaults = context.HttpContext.Response.StatusCode == 429
                && context.HttpContext.Response.Headers.RetryAfter == "2";
            context.HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.HttpContext.Response.Headers.RetryAfter = "3";
            return new ValueTask(context.HttpContext.Response.WriteAsync("custom", cancellationToken));
        }

        await using var app = new RateLimitingApplication(options =>
        {
            if (configureBefore) options.OnRejected = Reject;
            options.AddRespirePartitionedLimiter("api", _ => "shared", _ => limiter);
            if (!configureBefore) options.OnRejected = Reject;
        });
        using var http = app.CreateClient();
        using var response = await http.GetAsync("/api");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(response.Headers.GetValues("Retry-After").Single()).IsEqualTo("3");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("custom");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(sawDefaults).IsTrue();
    }

    [Test]
    public async Task PoliciesWithSamePartitionKeyUseSeparateFactoriesAndCacheEachLimiter()
    {
        await using var first = new TestLimiter();
        await using var second = new TestLimiter();
        var firstCalls = 0;
        var secondCalls = 0;
        await using var app = new RateLimitingApplication(options =>
        {
            options.AddRespirePartitionedLimiter("api", _ => 0, _ => { firstCalls++; return first; });
            options.AddRespirePartitionedLimiter("other", _ => 0, _ => { secondCalls++; return second; });
        });
        using var http = app.CreateClient();
        using var firstResponse = await http.GetAsync("/api");
        using var secondResponse = await http.GetAsync("/api");
        using var otherResponse = await http.GetAsync("/other");
        await Assert.That(firstCalls).IsEqualTo(1);
        await Assert.That(secondCalls).IsEqualTo(1);
        await Assert.That(first.Attempts).IsEqualTo(2);
        await Assert.That(second.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task StoppingHostDisposesCachedLimiter()
    {
        var limiter = new TestLimiter();
        await using (var app = new RateLimitingApplication(options =>
            options.AddRespirePartitionedLimiter("api", _ => "shared", _ => limiter)))
        {
            using var http = app.CreateClient();
            using var response = await http.GetAsync("/api");
            await Assert.That(limiter.Disposals).IsEqualTo(0);
        }
        await Assert.That(limiter.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task QueuedRequestCanCompleteBeforeHostStops()
    {
        var limiter = new TestLimiter(waitForPermit: true);
        await using (var app = new RateLimitingApplication(options =>
            options.AddRespirePartitionedLimiter("api", _ => "shared", _ => limiter)))
        {
            using var http = app.CreateClient();
            var request = http.GetAsync("/api");
            await limiter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await Assert.That(limiter.Disposals).IsEqualTo(0);
            limiter.PendingLease.TrySetResult(new TestLease(null, acquired: true));

            using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(limiter.Disposals).IsEqualTo(0);
        }
        await Assert.That(limiter.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task IdleEvictionDisposesLimiterAndNextRequestCreatesReplacement()
    {
        var first = new TestLimiter();
        var second = new TestLimiter();
        var factoryCalls = 0;
        await using (var app = new RateLimitingApplication(options =>
            options.AddRespirePartitionedLimiter("api", _ => "shared",
                _ => Interlocked.Increment(ref factoryCalls) == 1 ? first : second)))
        {
            using var http = app.CreateClient();
            using var firstResponse = await http.GetAsync("/api");
            // Report an already-idle partition so the middleware's normal heartbeat evicts it.
            first.MarkIdle();
            await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using var secondResponse = await http.GetAsync("/api");
            await Assert.That(factoryCalls).IsEqualTo(2);
            await Assert.That(first.Attempts).IsEqualTo(1);
            await Assert.That(first.Disposals).IsEqualTo(1);
            await Assert.That(second.Attempts).IsEqualTo(1);
            await Assert.That(second.Disposals).IsEqualTo(0);
        }
        await Assert.That(first.Disposals).IsEqualTo(1);
        await Assert.That(second.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task RequestCancellationReachesAsynchronousAcquisition()
    {
        await using var limiter = new TestLimiter(waitForCancellation: true);
        await using var app = new RateLimitingApplication(options =>
            options.AddRespirePartitionedLimiter("api", _ => "shared", _ => limiter));
        using var http = app.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var request = http.GetAsync("/api", cancellation.Token);
        await limiter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await request).Throws<OperationCanceledException>();
        await Assert.That(limiter.AcquisitionToken.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task RegistrationRejectsInvalidArgumentsAndDuplicatePolicyNames()
    {
        var options = new RateLimiterOptions();
        await Assert.That(() => options.AddRespirePartitionedLimiter<int>("api", null!, _ => null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => options.AddRespirePartitionedLimiter<int>("api", _ => 0, null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => options.AddRespirePartitionedLimiter("", _ => 0, _ => null!))
            .Throws<ArgumentException>();
        options.AddRespirePartitionedLimiter("api", _ => 0, _ => null!);
        await Assert.That(() => options.AddRespirePartitionedLimiter("api", _ => 1, _ => null!))
            .Throws<ArgumentException>();
    }

    private sealed class TestLimiter(TimeSpan? retryAfter = null, bool waitForCancellation = false,
        bool waitForPermit = false) : RateLimiter
    {
        private int _idle;
        public int Disposals;
        public int Attempts;
        public CancellationToken AcquisitionToken;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RateLimitLease> PendingLease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void MarkIdle() => Volatile.Write(ref _idle, 1);
        public override TimeSpan? IdleDuration => Volatile.Read(ref _idle) != 0 ? TimeSpan.FromMinutes(1) : null;
        public override RateLimiterStatistics? GetStatistics() => null;
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => new TestLease(null);
        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            Attempts++;
            AcquisitionToken = cancellationToken;
            Entered.TrySetResult();
            if (waitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (waitForPermit) return await PendingLease.Task.WaitAsync(cancellationToken);
            return new TestLease(retryAfter);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref Disposals);
                if (waitForPermit) PendingLease.TrySetException(new ObjectDisposedException(nameof(TestLimiter)));
                Disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }
        protected override ValueTask DisposeAsyncCore()
        {
            Dispose(true);
            return default;
        }
    }

    private sealed class TestLease(TimeSpan? retryAfter, bool acquired = false) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames => retryAfter.HasValue ? [MetadataName.RetryAfter.Name] : [];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
    }
}
