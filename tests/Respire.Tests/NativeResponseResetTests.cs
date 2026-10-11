using System.Reflection;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class NativeResponseResetTests
{
    [Test]
    [Arguments("converted", false)]
    [Arguments("string", false)]
    [Arguments("bytes", false)]
    [Arguments("converted", true)]
    [Arguments("string", true)]
    [Arguments("bytes", true)]
    public async Task ThrowingDisposalClearsFieldsAndAllowsFreshCompletion(string shape, bool poolReset)
    {
        var source = Rent(shape);
        // Invalid element count injects a disposal failure without renting array storage.
        source.TrySetResult(RespValue.PooledAggregate(RespDataType.Array, new RespValue[1], 2));
        if (source is StringPendingResponseSource text) text.SetDirectResult("stale");
        if (source is BytesPendingResponseSource bytes) bytes.SetDirectResult([1]);
        try
        {
            if (poolReset)
            {
                // Exercise the policy directly while it still owns a response, independently
                // of GetResult's earlier cleanup. A throwing reset must reset the core too.
                var poolType = source.GetType().GetField("Pool", BindingFlags.Static | BindingFlags.NonPublic)!.FieldType;
                var policyType = poolType.GetGenericArguments()[1];
                var policy = Activator.CreateInstance(policyType)!;
                var error = await Assert.That(() => policyType.GetMethod("TryReset")!.Invoke(policy, [source]))
                    .ThrowsExactly<TargetInvocationException>();
                await Assert.That(error!.InnerException).IsTypeOf<IndexOutOfRangeException>();
                await Assert.That(source.CommandName).IsNull();
            }
            else
            {
                await Assert.That(() => Consume(source)).ThrowsExactly<IndexOutOfRangeException>();
                // Receive ownership retains only the operation label, not caller state.
                await Assert.That(source.CommandName).IsEqualTo("GET");
            }

            foreach (var name in new[] { "_response", "_hasResponse", "_state", "_converter", "_transferOwnership",
                         "_directResult", "_hasDirectResult", "_observation", "_observeErrors", "_duration" })
            {
                var field = source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field is null) continue;
                var empty = field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null;
                await Assert.That(field.GetValue(source)).IsEqualTo(empty);
            }

            if (poolReset)
            {
                var completed = source switch
                {
                    ConvertedPendingResponseSource<object, int> converted => converted.Task.IsCompleted,
                    StringPendingResponseSource textSource => textSource.Task.IsCompleted,
                    BytesPendingResponseSource bytesSource => bytesSource.Task.IsCompleted,
                    _ => throw new InvalidOperationException("Unknown response shape."),
                };
                await Assert.That(completed).IsFalse();
                // The reset policy has already discarded the caller's completion.
                source.ReleaseRef();
            }
        }
        finally { source.ReleaseRef(); }

        // Find this exact object without assuming shared pool order. Hold other rentals
        // until it is found, then verify a new command cannot see the old direct result.
        var rentals = new List<PendingResponse>();
        try
        {
            PendingResponse? reused = null;
            for (var i = 0; i <= 4096; i++)
            {
                var candidate = Rent(shape);
                rentals.Add(candidate);
                if (!ReferenceEquals(candidate, source)) continue;
                reused = candidate;
                break;
            }
            await Assert.That(reused).IsNotNull();
            var pending = Consume(reused!);
            await Assert.That(pending.IsCompleted).IsFalse();
            reused!.TrySetResult(RespValue.SimpleString("fresh"));
            await pending;
            reused.ReleaseRef();
            rentals.Remove(reused);
        }
        finally
        {
            foreach (var rental in rentals)
            {
                rental.TrySetResult(RespValue.SimpleString("fresh"));
                try { await Consume(rental); }
                finally { rental.ReleaseRef(); }
            }
        }
    }

    private static PendingResponse Rent(string shape) => shape switch
    {
        "converted" => ConvertedPendingResponseSource<object, int>.Rent(new object(),
            ConvertFreshResponse,
            false, "GET", observeErrors: false, observation: ErrorObservation.StartFailure()),
        "string" => StringPendingResponseSource.Rent("GET", observeErrors: false,
            observation: ErrorObservation.StartFailure()),
        _ => BytesPendingResponseSource.Rent("GET", observeErrors: false,
            observation: ErrorObservation.StartFailure()),
    };

    private static int ConvertFreshResponse(object state, in RespValue value)
    {
        if (value.Type == RespDataType.Array) return 0;
        if (value.AsString() == "fresh") return 42;
        throw new InvalidOperationException("Stale response.");
    }

    private static async Task Consume(PendingResponse source)
    {
        switch (source)
        {
            case ConvertedPendingResponseSource<object, int> converted:
                await Assert.That(await converted.Task).IsEqualTo(42);
                break;
            case StringPendingResponseSource text:
                await Assert.That(await text.Task).IsEqualTo("fresh");
                break;
            case BytesPendingResponseSource bytes:
                await Assert.That(System.Text.Encoding.UTF8.GetString((await bytes.Task)!)).IsEqualTo("fresh");
                break;
        }
    }
}
