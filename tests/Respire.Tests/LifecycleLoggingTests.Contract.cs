using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Respire.Tests;

public partial class LifecycleLoggingTests
{
    // The snapshot preserves the original templates and levels, with stable event IDs.
    // Exercise each generated method independently of its attribute metadata.
    public sealed record LogContract(string Method, string Level, int EventId, string? EventName,
        string Template, string LegacySource);

    private sealed record LogCatalog(string LegacyCommit, LogContract[] Messages);

    public static IEnumerable<LogContract> CatalogMessages()
    {
        using var resource = typeof(LifecycleLoggingTests).Assembly
            .GetManifestResourceStream("Respire.Tests.LifecycleLogContract.json")
            ?? throw new InvalidOperationException("Missing legacy logging contract snapshot.");
        return JsonSerializer.Deserialize<LogCatalog>(resource)!.Messages;
    }

    [Test]
    public async Task LoggingContractSnapshotCoversEveryGeneratedMessage()
    {
        // Dynamic-level Sentinel events are covered separately.
        var methods = typeof(RespireLog).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttribute<LoggerMessageAttribute>() is not null
                && method.Name != nameof(RespireLog.SentinelEvent))
            .Select(method => method.Name).ToArray();
        await Assert.That(CatalogMessages().Select(message => message.Method).ToArray()).IsEquivalentTo(methods);
    }

    [Test]
    [MethodDataSource(nameof(CatalogMessages))]
    public async Task EveryGeneratedMessagePreservesStructuredContract(LogContract contract)
    {
        var method = typeof(RespireLog).GetMethod(contract.Method, BindingFlags.Static | BindingFlags.NonPublic)!;
        var values = method.GetParameters().Skip(1).SkipLast(1)
            .Select((parameter, index) => SampleLogValue(parameter.ParameterType, index)).ToArray();
        var error = new InvalidOperationException("Original diagnostic cause.");
        var expected = Capture(logger => logger.Log(Enum.Parse<LogLevel>(contract.Level),
            new EventId(contract.EventId, contract.EventName), error, contract.Template, values));
        var actual = Capture(logger => method.Invoke(null, [logger, .. values, error]));
        await Assert.That(actual.Level).IsEqualTo(expected.Level);
        await Assert.That(actual.EventId.Id).IsEqualTo(expected.EventId.Id);
        await Assert.That(actual.EventId.Name).IsEqualTo(expected.EventId.Name);
        await Assert.That(ReferenceEquals(actual.Error, error)).IsTrue();
        await Assert.That(actual.Message).IsEqualTo(expected.Message);
        await Assert.That(actual.State).IsEquivalentTo(expected.State);
        using var disabled = new ProbeFactory(enabled: false);
        method.Invoke(null, [disabled.CreateLogger("Respire.Lifecycle"), .. values, error]);
        await Assert.That(disabled.LogCalls).IsEqualTo(0);
    }

    private static object SampleLogValue(Type type, int index)
    {
        if (type == typeof(string)) return $"value-{index}";
        if (type == typeof(int)) return 10 + index;
        if (type == typeof(long)) return 100L + index;
        if (type == typeof(double)) return 1.25 + index;
        if (type == typeof(bool)) return index % 2 == 0;
        if (type == typeof(TimeSpan)) return TimeSpan.FromMilliseconds(25 + index);
        if (type == typeof(RespireEndpoint)) return new RespireEndpoint($"redis-{index}.example", 6379 + index);
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0)!;
        throw new InvalidOperationException($"Add a logging contract sample for {type}.");
    }
}
