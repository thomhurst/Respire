using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ExpiryApiContractTests
{
    [Test]
    public async Task CommandExpiryInputsUseOneNameAndType()
    {
        var offenders = typeof(IRespireClient).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName && method.DeclaringType != typeof(RespireExpiry))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.Name is "expiry" or "ttl" || parameter.ParameterType == typeof(RespireExpiry))
            .Where(parameter => parameter.Name != "expiry" || parameter.ParameterType != typeof(RespireExpiry))
            .Select(parameter => $"{parameter.Member.DeclaringType!.Name}.{parameter.Member.Name}: {parameter.ParameterType.Name} {parameter.Name}")
            .Order().ToArray();

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task LockAcquisitionUsesRelativeLeaseDuration()
    {
        var methods = new[] { typeof(IRespireLockCommands), typeof(RespireLockCommandExtensions) }
            .SelectMany(type => type.GetMethods())
            .Where(method => method.Name is "AcquireAsync" or "AcquireOrThrowAsync" or "TryTakeAsync")
            .ToArray();

        await Assert.That(methods).IsNotEmpty();
        foreach (var method in methods)
        {
            var duration = method.GetParameters().First(parameter => parameter.ParameterType == typeof(TimeSpan));
            await Assert.That(duration.Name).IsEqualTo("leaseDuration");
        }
    }
}
