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
                    || type == typeof(RespireWatchedTransaction)
                    || type == typeof(RespireTransactionBase)
                    || type == typeof(IRespireCommandQueue)
                    || type.IsInterface && type.Name.StartsWith("IRespireBatch", StringComparison.Ordinal)))
            .ToArray();

        await Assert.That(apiTypes.Contains(typeof(IRespireBatchStringCommands))).IsTrue();

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
        var commit = typeof(RespireTransaction).GetMethod("CommitAsync", [typeof(CancellationToken)]);
        await Assert.That(commit).IsNotNull();
        await Assert.That(commit!.ReturnType)
            .IsEqualTo(typeof(ValueTask));
        var watchedCommit = typeof(RespireWatchedTransaction)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == "CommitAsync");
        await Assert.That(watchedCommit.ReturnType)
            .IsEqualTo(typeof(ValueTask<bool>));
    }

    [Test]
    public async Task BatchAndTransactionShareCommandQueueInterface()
    {
        await Assert.That(typeof(IRespireCommandQueue).IsAssignableFrom(typeof(RespireBatch))).IsTrue();
        await Assert.That(typeof(IRespireCommandQueue).IsAssignableFrom(typeof(RespireTransaction))).IsTrue();
        await Assert.That(typeof(IRespireCommandQueue).IsAssignableFrom(typeof(RespireWatchedTransaction))).IsTrue();

        var facetNames = typeof(IRespireCommandQueue)
            .GetProperties()
            .Select(static property => property.Name)
            .Order()
            .ToArray();

        await Assert.That(facetNames).IsEquivalentTo(new[]
        {
            "Arrays",
            "Bitmaps",
            "Functions",
            "Geo",
            "Hashes",
            "HyperLogLog",
            "Keys",
            "Lists",
            "PathHashes",
            "Scripts",
            "Server",
            "Sets",
            "SortedSets",
            "Strings",
            "Streams",
            "VectorSets",
        });

#pragma warning disable RESP010 // This isolated client only constructs deferred queues; the test never connects or sends them.
        await using var client = RespireClient.Create("localhost:6379");
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();

        QueueAggregate(batch);
        QueueAggregate(transaction);

        await Assert.That(batch.Count).IsEqualTo(3);
        await Assert.That(transaction.Count).IsEqualTo(3);
    }

    private static void QueueAggregate(IRespireCommandQueue queue)
    {
        _ = queue.Hashes.Set("aggregate", "status", "ready");
        _ = queue.Set("aggregate:version", 1);
        _ = queue.Expire("aggregate", RespireExpiry.In(TimeSpan.FromMinutes(5)));
    }
}
