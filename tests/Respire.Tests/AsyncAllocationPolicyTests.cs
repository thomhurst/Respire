using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class AsyncAllocationPolicyTests
{
    [Test]
    public async Task HotAsyncValueTaskMethodsUsePoolingBuilder()
    {
        Type[] commandTypes = [typeof(StreamCommands), typeof(ScriptCommands)];
        var coreMethods = new HashSet<(Type Type, string Name)>
        {
            (typeof(RespireClient), "ExecuteCatalogAsync"),
            (typeof(RespireClient), "ExecuteCatalogFireAndForgetAsync"),
            (typeof(RespireClient), "ExecuteRawAsync"),
            (typeof(RespireClient), "ExecuteRawFireAndForgetAsync"),
            (typeof(RespireClient), "ExecuteInterpolatedAsync"),
            (typeof(RespireClient), "ExecuteInterpolatedFireAndForgetAsync"),
            (typeof(RespireClient), "SendStoredProcedureAsync"),
            (typeof(RespireClient), "SendFireAndForgetOnConnectionInstrumentedAsync"),
            (typeof(RespireClient), "SendFireAndForgetClusterAsync"),
            (typeof(RespireClient), "SendOnConnectionInstrumentedAsync"),
            (typeof(RespireClient), "ExecuteScriptAsync"),
            (typeof(RespireClient), "ExecuteTrackedClusterScriptAsync"),
            (typeof(RespireClient), "ExecuteTrackedClusterCommandAsync"),
            (typeof(RespireClient), "ExecuteScriptOnConnectionAsync"),
            (typeof(RespireClient), "ExecuteScriptOnConnectionCoreAsync"),
            (typeof(RespireConnection), "WaitForInflightCapacityAsync"),
        };

        var candidates = commandTypes
            .SelectMany(static type => type.GetMethods(AllMethods))
            .Concat(coreMethods.Select(static candidate => candidate.Type.GetMethods(AllMethods)
                .Single(method => method.Name == candidate.Name)))
            .Where(static method => method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
            .Where(static method => IsValueTask(method.ReturnType));

        var offenders = candidates
            .Where(static method => method.GetCustomAttribute<AsyncMethodBuilderAttribute>()?.BuilderType.Name
                .StartsWith(nameof(PoolingAsyncValueTaskMethodBuilder), StringComparison.Ordinal) != true)
            .Select(static method => $"{method.DeclaringType!.Name}.{method.Name}")
            .Order()
            .ToArray();

        await Assert.That(offenders).IsEmpty();
    }

    private const BindingFlags AllMethods =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool IsValueTask(Type type)
        => type == typeof(ValueTask)
            || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>);
}
