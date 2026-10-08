using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Authentication;
using System.Threading.Tasks.Sources;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class ErrorMetricFoundationTests
{
    [Test]
    [Arguments(-1, 0)]
    [Arguments(0, 0)]
    [Arguments(16, 16)]
    [Arguments(17, 17)]
    [Arguments(int.MaxValue, int.MaxValue)]
    public async Task RetryCountsKeepExactValuesWithoutGrowingTheBoxingCache(int supplied, int expected)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        RespireTelemetry.RecordError(new InvalidOperationException(), false, supplied);
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(expected);
    }

    [Test]
    [Arguments("server", "server", "WRONGTYPE")]
    [Arguments("unknown-server", "server", null)]
    [Arguments("auth-server", "auth", "NOPERM")]
    [Arguments("auth-wrapper", "auth", "WRONGPASS")]
    [Arguments("tls", "tls", null)]
    [Arguments("socket", "network", null)]
    [Arguments("io", "network", null)]
    [Arguments("connection", "network", null)]
    [Arguments("timeout", "network", null)]
    [Arguments("other", "other", null)]
    [Arguments("cancellation", "other", null)]
    public async Task ClassificationPublishesOnlyBoundedTags(string kind, string category, string? code)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        Exception error = kind switch
        {
            "server" => new RespireServerException("WRONGTYPE private key/value"),
            "unknown-server" => new RespireServerException("PRIVATE_CUSTOMER_123 private details"),
            "auth-server" => new RespireServerException("NOPERM private credentials"),
            "auth-wrapper" => new RespireAuthenticationException("private credentials", new RespireServerException("WRONGPASS private secret")),
            "tls" => new AuthenticationException("private certificate"),
            "socket" => new SocketException((int)SocketError.ConnectionRefused),
            "io" => new IOException("private path"),
            "connection" => new RespireConnectionException("private endpoint"),
            "timeout" => new RespireTimeoutException("PRIVATE_COMMAND", TimeSpan.FromSeconds(1)),
            "cancellation" => new OperationCanceledException(),
            _ => new InvalidOperationException("private payload"),
        };
        RespireTelemetry.RecordError(error, internallyHandled: true, retryAttempts: 37);
        var item = capture.Items.Single();
        await Assert.That(item.Count).IsEqualTo(1L);
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo(category);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(37);
        await Assert.That(item.Tags["error.type"]).IsEqualTo(error.GetType().FullName);
        await Assert.That(item.Tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(item.Tags.ContainsKey("db.response.status_code")).IsEqualTo(code is not null);
        if (code is not null) await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo(code);
        await Assert.That(item.Tags.Values.OfType<string>().Any(value => value.Contains("private", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ExplicitWrapperMeaningAndAggregateCardinalityArePreserved(bool transparent)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var cause = new RespireServerException("NOPERM private details");
        var wrapper = new DeclaredWrapper(cause, transparent);
        RespireTelemetry.RecordError(new AggregateException(wrapper), false);
        var first = capture.Items.Single();
        await Assert.That(first.Tags["error.type"]).IsEqualTo(transparent ? cause.GetType().FullName : wrapper.GetType().FullName);
        await Assert.That(first.Tags["redis.client.errors.category"]).IsEqualTo(transparent ? "auth" : "other");
        RespireTelemetry.RecordError(new RespireConnectionException("candidates", new AggregateException(cause, new IOException())), false);
        var second = capture.Items.Last();
        await Assert.That(second.Tags["error.type"]).IsEqualTo(typeof(RespireConnectionException).FullName);
        await Assert.That(second.Tags["redis.client.errors.category"]).IsEqualTo("network");
    }

    [Test]
    public async Task DeepWrapperClassificationHasBoundedWork()
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        Exception error = new RespireServerException("NOPERM private details");
        for (var i = 0; i < 20; i++) error = new DeclaredWrapper(error, true);
        RespireTelemetry.RecordError(error, false);
        await Assert.That(capture.Items.Single().Tags["error.type"]).IsEqualTo(typeof(DeclaredWrapper).FullName);
        await Assert.That(capture.Items.Single().Tags.ContainsKey("db.response.status_code")).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FinalOwnerConsumesExactlyOnceAndKeepsOriginalFailure(bool asynchronous, bool generic)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture(throwOnMeasurement: true);
        var expected = new InvalidOperationException("application failure");
        var source = new ResponseSource(expected, asynchronous);
        Exception? actual = null;
        if (generic)
        {
            var pending = RespireTelemetry.ObserveFinalError(new ValueTask<int>(source, 0), 3);
            if (asynchronous) source.Complete();
            try { await pending; } catch (Exception error) { actual = error; }
        }
        else
        {
            var pending = RespireTelemetry.ObserveFinalError(new ValueTask(source, 0), 3);
            if (asynchronous) source.Complete();
            try { await pending; } catch (Exception error) { actual = error; }
        }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(source.Consumptions).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(3);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SuccessConsumesExactlyOnceWithoutErrorPublication(bool asynchronous, bool generic)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        var source = new ResponseSource(null, asynchronous);
        if (generic)
        {
            var pending = RespireTelemetry.ObserveFinalError(new ValueTask<int>(source, 0));
            if (asynchronous) source.Complete();
            await Assert.That(await pending).IsEqualTo(42);
        }
        else
        {
            var pending = RespireTelemetry.ObserveFinalError(new ValueTask(source, 0));
            if (asynchronous) source.Complete();
            await pending;
        }
        await Assert.That(source.Consumptions).IsEqualTo(1);
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationRetainsExceptionAndToken(bool generic)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        var source = new ResponseSource(expected, true);
        Exception? actual = null;
        if (generic)
        {
            var pending = RespireTelemetry.ObserveFinalError(new ValueTask<int>(source, 0)).AsTask();
            source.Complete();
            try { await pending; } catch (Exception error) { actual = error; }
            await Assert.That(pending.IsCanceled).IsTrue();
        }
        else
        {
            var pending = RespireTelemetry.ObserveFinalError(new ValueTask(source, 0)).AsTask();
            source.Complete();
            try { await pending; } catch (Exception error) { actual = error; }
            await Assert.That(pending.IsCanceled).IsTrue();
        }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InflightFailureUsesEventTimeConfiguration(bool initiallyEnabled)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = initiallyEnabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = RespireTelemetry.ObserveFinalError(new ValueTask<int>(completion.Task));
        RespireMetrics.Configure(new() { Groups = initiallyEnabled ? RespireMetricGroups.None : RespireMetricGroups.Resiliency });
        completion.SetException(new InvalidOperationException());
        try { await pending; } catch (InvalidOperationException) { }
        await Assert.That(capture.Items.Count).IsEqualTo(initiallyEnabled ? 0 : 1);
    }

    [Test]
    public async Task TypeNamesHaveBoundedBudgetWithoutRetainingCollectibleTypes()
    {
        var cache = new ErrorTypeNameCache(1);
        var cached = CacheCollectibleType(cache);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        await Assert.That(cached.Type.IsAlive).IsFalse();
        await Assert.That(cached.Name).IsEqualTo("CollectibleFoundationError");
        await Assert.That(cache.Get(typeof(InvalidOperationException))).IsEqualTo("_OTHER");
        await Assert.That(CacheCollectibleType(cache).Name).IsEqualTo(cached.Name);
        GC.KeepAlive(cache);
    }

    [Test]
    public async Task ConcurrentFirstTypeUsesShareOneBudget()
    {
        var cache = new ErrorTypeNameCache(2);
        Type[] types = [typeof(IOException), typeof(SocketException), typeof(AuthenticationException), typeof(InvalidOperationException)];
        using var start = new ManualResetEventSlim();
        var pending = types.Select(type => Task.Run(() => { start.Wait(); return cache.Get(type); })).ToArray();
        start.Set();
        var names = await Task.WhenAll(pending);
        await Assert.That(names.Count(name => name != "_OTHER")).IsEqualTo(2);
        for (var i = 0; i < types.Length; i++) await Assert.That(cache.Get(types[i])).IsEqualTo(names[i]);
    }

    [Test]
    [Arguments("record")]
    [Arguments("observe")]
    public async Task ColdPublicationFailureDoesNotPoisonLaterReports(string boundary)
    {
        var context = new AssemblyLoadContext("cold-foundation-errors", isCollectible: true);
        Meter? meter = null;
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(RespireClient).Assembly.Location);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var telemetry = assembly.GetType("Respire.Internal.RespireTelemetry", true)!;
            meter = (Meter)telemetry.GetField("Meter", flags)!.GetValue(null)!;
            var record = telemetry.GetMethod("RecordError", flags)!;
            var original = new InvalidOperationException("application failure");
            var publications = 0;
            using (var rejecting = new MeterListener())
            {
                rejecting.InstrumentPublished = (instrument, _) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "redis.client.errors")
                    {
                        publications++;
                        throw new InvalidOperationException("exporter rejects publication");
                    }
                };
                rejecting.Start();
                if (boundary == "record") record.Invoke(null, [original, false, 0]);
                else
                {
                    var observe = telemetry.GetMethods(flags).Single(method => method.Name == "ObserveFinalError" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(int));
                    var pending = (ValueTask<int>)observe.Invoke(null, [ValueTask.FromException<int>(original), 0])!;
                    Exception? actual = null;
                    try { await pending; } catch (Exception error) { actual = error; }
                    await Assert.That(ReferenceEquals(actual, original)).IsTrue();
                }
            }
            await Assert.That(publications).IsEqualTo(1);
            var measurements = 0;
            using var accepting = new MeterListener();
            accepting.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "redis.client.errors") listener.EnableMeasurementEvents(instrument);
            };
            accepting.SetMeasurementEventCallback<long>((_, count, _, _) => measurements += (int)count);
            accepting.Start();
            record.Invoke(null, [original, false, 0]);
            await Assert.That(measurements).IsEqualTo(1);
        }
        finally { meter?.Dispose(); context.Unload(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ColdSuccessDoesNotPublishAnErrorInstrument(bool generic)
    {
        var context = new AssemblyLoadContext("cold-foundation-success", isCollectible: true);
        Meter? meter = null;
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(RespireClient).Assembly.Location);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var telemetry = assembly.GetType("Respire.Internal.RespireTelemetry", true)!;
            meter = (Meter)telemetry.GetField("Meter", flags)!.GetValue(null)!;
            var publications = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "redis.client.errors") publications++;
            };
            listener.Start();
            var observe = telemetry.GetMethods(flags).Single(method => method.Name == "ObserveFinalError" && method.IsGenericMethodDefinition == generic);
            if (generic)
            {
                var pending = (ValueTask<int>)observe.MakeGenericMethod(typeof(int)).Invoke(null, [new ValueTask<int>(42), 0])!;
                await Assert.That(await pending).IsEqualTo(42);
            }
            else await (ValueTask)observe.Invoke(null, [ValueTask.CompletedTask, 0])!;
            await Assert.That(publications).IsEqualTo(0);
            telemetry.GetMethod("RecordError", flags)!.Invoke(null, [new InvalidOperationException(), false, 0]);
            await Assert.That(publications).IsEqualTo(1);
        }
        finally { meter?.Dispose(); context.Unload(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WarmSuccessAndErrorPublicationAllocateNothing(bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture(retain: false);
        var error = new InvalidOperationException();
        _ = Measure(error, false);
        _ = Measure(error, true);
        var actual = AllocationMeasurement.WithoutConcurrentGc(() => Measure(error, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => Measure(error, true));
        await Assert.That(actual).IsEqualTo(0L);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(Exception error, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            var result = RespireTelemetry.ObserveFinalError(new ValueTask<int>(42)).GetAwaiter().GetResult();
            RespireTelemetry.ObserveFinalError(ValueTask.CompletedTask).GetAwaiter().GetResult();
            RespireTelemetry.RecordError(error, false, 3);
            if (allocate) Volatile.Write(ref _allocationAnchor, new byte[37]);
            if (result != 42) throw new InvalidOperationException("success result changed");
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static object? _allocationAnchor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Type, string Name) CacheCollectibleType(ErrorTypeNameCache cache)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Respire.FoundationErrorType"), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("errors").DefineType("CollectibleFoundationError", TypeAttributes.Public, typeof(Exception)).CreateType()!;
        return (new WeakReference(type), cache.Get(type));
    }

    private sealed class DeclaredWrapper(Exception cause, bool transparent) : RespireException("wrapper", cause)
    {
        internal override Exception? ErrorCause => transparent ? InnerException : null;
    }

    // Succeeded status deliberately leaves error translation to GetResult, like native RESP sources.
    private sealed class ResponseSource(Exception? error, bool asynchronous) : IValueTaskSource<int>, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<int> _completion = new() { RunContinuationsAsynchronously = true };
        internal int Consumptions;
        internal void Complete() => _completion.SetResult(42);
        public ValueTaskSourceStatus GetStatus(short token) => asynchronous ? _completion.GetStatus(token) : ValueTaskSourceStatus.Succeeded;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _completion.OnCompleted(continuation, state, token, flags);
        public int GetResult(short token)
        {
            Interlocked.Increment(ref Consumptions);
            if (asynchronous) _ = _completion.GetResult(token);
            if (error is not null) throw error;
            return 42;
        }
        void IValueTaskSource.GetResult(short token) => _ = GetResult(token);
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal readonly ConcurrentQueue<(long Count, Dictionary<string, object?> Tags)> Items = new();
        internal Capture(bool throwOnMeasurement = false, bool retain = true)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, RespireTelemetry.Meter) && instrument.Name == "redis.client.errors") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, count, tags, _) =>
            {
                if (retain)
                {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var tag in tags) values.Add(tag.Key, tag.Value);
                    Items.Enqueue((count, values));
                }
                if (throwOnMeasurement) throw new InvalidOperationException("exporter rejects measurement");
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
