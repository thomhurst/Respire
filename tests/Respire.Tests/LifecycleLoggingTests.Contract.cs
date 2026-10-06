using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Respire.Tests;

public partial class LifecycleLoggingTests
{
    // The checked-in snapshot records the pre-conversion templates, levels and event IDs.
    // Reflection exercises every cached delegate without deriving expectations from its state.
    public sealed record LogContract(string Field, string Level, int EventId, string? EventName,
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
    public async Task LoggingContractSnapshotCoversEveryCachedMessage()
    {
        // Generated callbacks belong to pre-existing [LoggerMessage] methods. The dynamic
        // Sentinel event array has a separate test covering all seven levels.
        var fields = typeof(RespireLog).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType)
                && !field.Name.StartsWith("__", StringComparison.Ordinal))
            .Select(field => field.Name).ToArray();
        await Assert.That(CatalogMessages().Select(message => message.Field).ToArray()).IsEquivalentTo(fields);
    }

    [Test]
    [MethodDataSource(nameof(CatalogMessages))]
    public async Task EveryCachedMessagePreservesLegacyContract(LogContract contract)
    {
        var field = typeof(RespireLog).GetField(contract.Field, BindingFlags.Static | BindingFlags.NonPublic)!;
        var log = (Delegate)field.GetValue(null)!;
        var types = field.FieldType.GenericTypeArguments;
        var values = types.Skip(1).SkipLast(1).Select(SampleLogValue).ToArray();
        var error = new InvalidOperationException("Original diagnostic cause.");
        var expected = Capture(logger => logger.Log(Enum.Parse<LogLevel>(contract.Level),
            new EventId(contract.EventId, contract.EventName), error, contract.Template, values));
        var actual = Capture(logger => log.DynamicInvoke([logger, .. values, error]));
        await Assert.That(actual.Level).IsEqualTo(expected.Level);
        await Assert.That(actual.EventId.Id).IsEqualTo(expected.EventId.Id);
        await Assert.That(actual.EventId.Name).IsEqualTo(expected.EventId.Name);
        await Assert.That(ReferenceEquals(actual.Error, error)).IsTrue();
        await Assert.That(actual.Message).IsEqualTo(expected.Message);
        await Assert.That(actual.State).IsEquivalentTo(expected.State);
        using var disabled = new ProbeFactory(enabled: false);
        log.DynamicInvoke([disabled.CreateLogger("Respire.Lifecycle"), .. values, error]);
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
