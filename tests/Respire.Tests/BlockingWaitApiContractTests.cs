using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class BlockingWaitApiContractTests
{
    [Test]
    public async Task BlockingCommandWaitsAreRequiredNullableTimeSpans()
    {
        var surfaces = new[] { typeof(IRespireListCommands), typeof(IRespireSortedSetCommands), typeof(IRespireStreamCommands) };
        var methods = surfaces.SelectMany(type => type.GetMethods())
            .Where(method => method.Name is "LeftPopAsync" or "RightPopAsync" or "PopAsync" or "PopManyAsync"
                or "MoveAsync" or "MoveManyAsync" or "ReadAsync").ToArray();
        var waits = methods.SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.Name is "waitFor" or "timeout" or "wait"
                || parameter.ParameterType == typeof(TimeSpan) || parameter.ParameterType == typeof(TimeSpan?)).ToArray();

        await Assert.That(waits).IsNotEmpty();
        await Assert.That(waits.Any(parameter => ((MethodInfo)parameter.Member).IsGenericMethodDefinition)).IsTrue();
        foreach (var parameter in waits)
        {
            await Assert.That(parameter.Name).IsEqualTo("waitFor");
            await Assert.That(parameter.ParameterType).IsEqualTo(typeof(TimeSpan?));
            // Omitted arguments must never start an indefinite wait on the combined APIs.
            await Assert.That(parameter.IsOptional).IsFalse();
        }
        await Assert.That(typeof(RespireStreamReadOptions).GetProperty("WaitFor")!.PropertyType).IsEqualTo(typeof(TimeSpan?));
    }

    [Test]
    public async Task StreamOptionsDistinguishOmittedAndExplicitNullWaits()
    {
        var immediate = new RespireStreamReadOptions { Count = 2 };
        var indefinite = immediate with { WaitFor = null };
        await Assert.That(immediate.IsBlocking).IsFalse();
        await Assert.That(indefinite.IsBlocking).IsTrue();
        await Assert.That(indefinite.WaitFor).IsNull();
        await Assert.That(indefinite.GetBlockMilliseconds()).IsEqualTo(0L);
        await Assert.That(immediate.GetBlockMilliseconds()).IsNull();
        await Assert.That(indefinite with { Count = 3 }).IsNotEqualTo(new RespireStreamReadOptions { Count = 3 });
    }
}
