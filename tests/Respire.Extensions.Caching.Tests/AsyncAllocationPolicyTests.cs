using System.Reflection;
using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

public class AsyncAllocationPolicyTests
{
    [Test]
    public async Task HotValueTaskMethodsUsePoolingBuilder()
    {
        string[] methodNames = ["TryGetAsync", "SetCoreAsync", "RunGetScriptAsync"];
        var methods = typeof(RespireDistributedCache).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var offenders = methodNames
            .Select(name => methods.Single(method => method.Name == name))
            .Where(static method => method.GetCustomAttribute<AsyncMethodBuilderAttribute>()?.BuilderType.Name
                .StartsWith(nameof(PoolingAsyncValueTaskMethodBuilder), StringComparison.Ordinal) != true)
            .Select(static method => method.Name)
            .ToArray();

        await Assert.That(offenders).IsEmpty();
    }
}
