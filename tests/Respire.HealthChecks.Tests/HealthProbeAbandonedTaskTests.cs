using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Respire.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class HealthProbeAbandonedTaskTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LateProviderFaultIsObservedAfterTimeout(bool contract)
    {
        var observed = new ConcurrentDictionary<Exception, bool>();
        var lateFailure = new InvalidOperationException("abandoned provider");
        var controlFailure = new InvalidOperationException("unobserved positive control");
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var error in args.Exception.Flatten().InnerExceptions) observed.TryAdd(error, true);
            args.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var abandoned = await AbandonProviderAsync(contract, lateFailure);
            var control = CreateUnobservedFault(controlFailure);
            for (var attempt = 0; attempt < 10 && (abandoned.IsAlive || control.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Yield();
            }
            await Assert.That(abandoned.IsAlive).IsFalse();
            await Assert.That(control.IsAlive).IsFalse();
            await Assert.That(observed.ContainsKey(controlFailure)).IsTrue();
            await Assert.That(observed.ContainsKey(lateFailure)).IsFalse();
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnobservedFault(Exception error) => new(Task.FromException(error));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> AbandonProviderAsync(bool contract, Exception error)
    {
        IRespireClient client = contract
            ? DispatchProxy.Create<RespireHealthProbeContractTests.IProbeClient, RespireHealthProbeContractTests.ProbeClientProxy>()
            : DispatchProxy.Create<IRespireClient, RespireHealthProbeContractTests.ProbeClientProxy>();
        var proxy = (RespireHealthProbeContractTests.ProbeClientProxy)client;
        proxy.Connected = true;
        var pending = new TaskCompletionSource<RespireHealthProbeResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ping = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        void RegisterCancellation(CancellationToken token) => registration = token.Register(() => cancellationFinished.SetResult());
        proxy.Probe = (_, token) => { RegisterCancellation(token); return new(pending.Task); };
        proxy.Ping = token => { RegisterCancellation(token); return new(ping.Task); };
        var context = new HealthCheckContext { Registration = new("respire", _ => throw new NotSupportedException(), HealthStatus.Unhealthy, null) };
        var result = await new RespireHealthCheck(client, new() { ProbeTimeout = TimeSpan.FromMilliseconds(1) })
            .CheckHealthAsync(context).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
        // Cancellation callbacks run in reverse registration order. WaitAsync registers after
        // the provider, so this waits until its cancellation callback has removed its observer.
        await cancellationFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registration.Dispose();
        if (contract) pending.SetException(error);
        else ping.SetException(error);
        return new WeakReference(contract ? pending.Task : ping.Task);
    }
}
