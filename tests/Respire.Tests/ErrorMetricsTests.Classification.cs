using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task ErrorTypeBudgetPreservesKnownNamesAndBoundsGenericTypes()
    {
        var cache = new ErrorTypeNameCache(2);
        var first = typeof(GenericMetricError<int>);
        var second = typeof(GenericMetricError<string>);
        var third = typeof(GenericMetricError<Guid>);
        await Assert.That(cache.Get(first)).IsEqualTo(first.FullName);
        await Assert.That(cache.Get(second)).IsEqualTo(second.FullName);
        await Assert.That(cache.Get(third)).IsEqualTo("_OTHER");
        await Assert.That(cache.Get(first)).IsEqualTo(first.FullName);
        await Assert.That(cache.Get(second)).IsEqualTo(second.FullName);
        await Assert.That(cache.Get(third)).IsEqualTo("_OTHER");
    }

    [Test]
    public async Task ErrorTypeBudgetIsSharedByConcurrentFirstUses()
    {
        var cache = new ErrorTypeNameCache(2);
        Type[] types = [typeof(int), typeof(string), typeof(Guid), typeof(DateTime), typeof(decimal), typeof(byte)];
        var errors = types.Select(type => typeof(GenericMetricError<>).MakeGenericType(type)).ToArray();
        using var start = new ManualResetEventSlim();
        var pending = errors.Select(type => Task.Run(() => { start.Wait(); return cache.Get(type); })).ToArray();
        start.Set();
        var names = await Task.WhenAll(pending);
        await Assert.That(names.Count(name => name != "_OTHER")).IsEqualTo(2);
        for (var i = 0; i < errors.Length; i++)
            await Assert.That(cache.Get(errors[i])).IsEqualTo(names[i]);
    }

    [Test]
    public async Task ErrorTypeBudgetRetainsNamesWithoutRetainingCollectibleTypes()
    {
        var cache = new ErrorTypeNameCache(1);
        var first = CacheCollectibleType(cache, "BoundedCollectibleMetricError");
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        await Assert.That(first.Type.IsAlive).IsFalse();
        await Assert.That(first.Name).IsEqualTo("BoundedCollectibleMetricError");
        await Assert.That(cache.Get(typeof(GenericMetricError<int>))).IsEqualTo("_OTHER");
        // A new collectible type with the same canonical name uses the existing label budget.
        var replacement = CacheCollectibleType(cache, "BoundedCollectibleMetricError");
        await Assert.That(replacement.Name).IsEqualTo(first.Name);
        GC.KeepAlive(cache);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewExceptionWrappersDeclareWhetherTheirCauseIsMeaningful(bool unwrap)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var error = new DeclaredMetricWrapper(new RespireServerException("NOPERM private detail"), unwrap);
        RespireTelemetry.RecordError(error, internallyHandled: false);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(
            unwrap ? typeof(RespireServerException).FullName : typeof(DeclaredMetricWrapper).FullName);
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo(unwrap ? "auth" : "other");
        await Assert.That(item.Tags.ContainsKey("db.response.status_code")).IsEqualTo(unwrap);
        if (unwrap) await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("NOPERM");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Type, string Name) CacheCollectibleType(ErrorTypeNameCache cache, string name)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Respire.BoundedErrorMetric"),
            AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("errors").DefineType(name, TypeAttributes.Public, typeof(Exception))
            .CreateType()!;
        return (new WeakReference(type), cache.Get(type));
    }

    private sealed class GenericMetricError<T> : Exception;

    private sealed class DeclaredMetricWrapper(Exception cause, bool unwrap) : RespireException("wrapper", cause)
    {
        internal override Exception? ErrorCause => unwrap ? InnerException : null;
    }
}
