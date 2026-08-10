using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class DeferredApiNamingTests
{
    [Test]
    public async Task DeferredProducingMethodsDoNotUseAsyncSuffix()
    {
        var apiTypes = typeof(RespireBatch).Assembly.GetTypes()
            .Where(type => type.IsPublic
                && (type == typeof(RespireBatch)
                    || type == typeof(RespireTransaction)
                    || type.IsInterface && type.Name.StartsWith("IBatch", StringComparison.Ordinal)));

        var offenders = apiTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(RespirePending<>))
            .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .Distinct()
            .Order()
            .ToArray();

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task BatchExecutesWhileTransactionCommits()
    {
        var batchMethods = typeof(RespireBatch).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(batchMethods.Any(method => method.Name == "ExecuteAsync")).IsTrue();
        await Assert.That(batchMethods.Any(method => method.Name == "SendAsync")).IsFalse();
        await Assert.That(typeof(RespireTransaction).GetMethod("CommitAsync")).IsNotNull();
    }
}
