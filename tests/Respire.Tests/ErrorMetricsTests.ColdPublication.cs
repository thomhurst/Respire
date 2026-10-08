using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.Loader;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [Arguments("record")]
    [Arguments("discard")]
    [Arguments("observe")]
    [Arguments("rent")]
    public async Task ColdErrorPublicationCannotReplaceAnApplicationFailure(string boundary)
    {
        // A fresh copy supplies genuinely cold instrument state regardless of test order.
        // Framework dependencies remain shared, so the actual MeterListener sees publication.
        var context = new AssemblyLoadContext("cold-error-publication", isCollectible: true);
        Meter? meter = null;
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(RespireClient).Assembly.Location);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var telemetry = assembly.GetType("Respire.Internal.RespireTelemetry", throwOnError: true)!;
            meter = (Meter)telemetry.GetField("Meter", flags)!.GetValue(null)!;
            var observation = telemetry.GetNestedType("ErrorObservation", BindingFlags.NonPublic)!;
            var record = telemetry.GetMethod("RecordError", flags)!;
            var original = new InvalidOperationException("Original application failure.");
            var publications = 0;
            using (var rejecting = new MeterListener())
            {
                rejecting.InstrumentPublished = (instrument, _) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "redis.client.errors")
                    {
                        publications++;
                        throw new InvalidOperationException("Rejected cold instrument publication.");
                    }
                };
                rejecting.Start();
                switch (boundary)
                {
                    case "record":
                        record.Invoke(null, [original, false, 0]);
                        break;
                    case "discard":
                        var valueType = assembly.GetType("Respire.Protocol.RespValue", throwOnError: true)!;
                        using (var value = (IDisposable)valueType.GetMethod("Error", [typeof(string)])!
                            .Invoke(null, ["ERR discarded error"])!)
                            telemetry.GetMethod("RecordDiscardedError", flags)!.Invoke(null, [value, "PING", 0]);
                        break;
                    case "observe":
                        var observe = telemetry.GetMethods(flags).Single(method =>
                            method.Name == "ObserveFinalError" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(int));
                        var pending = (ValueTask<int>)observe.Invoke(null,
                            [ValueTask.FromException<int>(original), Activator.CreateInstance(observation)])!;
                        var error = await Assert.That(async () => await pending).ThrowsExactly<InvalidOperationException>();
                        await Assert.That(ReferenceEquals(error, original)).IsTrue();
                        break;
                    case "rent":
                        using (var lease = (IDisposable)observation.GetMethod("Rent", flags)!.Invoke(null, [false])!) { }
                        break;
                }
            }
            await Assert.That(publications).IsEqualTo(1);

            // Removing a faulty publisher must permit future reports, rather than leave a
            // permanently faulted type initializer on every subsequent command boundary.
            var measurements = 0;
            using var accepting = new MeterListener();
            accepting.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            accepting.SetMeasurementEventCallback<long>((_, count, _, _) => measurements += (int)count);
            accepting.Start();
            record.Invoke(null, [original, false, 0]);
            await Assert.That(measurements).IsEqualTo(1);
        }
        finally
        {
            meter?.Dispose();
            context.Unload();
        }
    }
}
