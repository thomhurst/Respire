using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class RetryCategoryMetadataTests
{
    [Test]
    [Arguments("PING", RespireCommandRetryCategory.Always)]
    [Arguments("CONFIG GET", RespireCommandRetryCategory.Connection)]
    [Arguments("GET", RespireCommandRetryCategory.ReadOnly)]
    [Arguments("JSON.GET", RespireCommandRetryCategory.ReadOnly)]
    [Arguments("SETNX", RespireCommandRetryCategory.WriteChecked)]
    [Arguments("HSET", RespireCommandRetryCategory.WriteLastWins)]
    [Arguments("SET", RespireCommandRetryCategory.WriteAccumulating)]
    [Arguments("ZADD", RespireCommandRetryCategory.WriteAccumulating)]
    [Arguments("BITFIELD", RespireCommandRetryCategory.WriteAccumulating)]
    [Arguments("INCR", RespireCommandRetryCategory.WriteAccumulating)]
    [Arguments("GETDEL", RespireCommandRetryCategory.WriteAccumulating)]
    [Arguments("XREADGROUP", RespireCommandRetryCategory.WriteAccumulating)]
    [Arguments("CONFIG SET", RespireCommandRetryCategory.ServerAdmin)]
    [Arguments("FLUSHALL", RespireCommandRetryCategory.ServerAdmin)]
    [Arguments("EVAL", RespireCommandRetryCategory.Never)]
    [Arguments("EVAL_RO", RespireCommandRetryCategory.Never)]
    [Arguments("EVALSHA_RO", RespireCommandRetryCategory.Never)]
    [Arguments("FCALL_RO", RespireCommandRetryCategory.Never)]
    [Arguments("FT.CURSOR READ", RespireCommandRetryCategory.Never)]
    [Arguments("FT.AGGREGATE", RespireCommandRetryCategory.Never)]
    [Arguments("FT.PROFILE", RespireCommandRetryCategory.Never)]
    [Arguments("CLIENT TRACKING", RespireCommandRetryCategory.Never)]
    [Arguments("EXEC", RespireCommandRetryCategory.Never)]
    [Arguments("SHUTDOWN", RespireCommandRetryCategory.Never)]
    public async Task AuditedRiskIncludesDangerousAndOptionSensitiveCommands(
        string name, RespireCommandRetryCategory expected)
    {
        var descriptor = RespireCommands.All.ToArray().Single(command => command.Name == name);
        await Assert.That(descriptor.RetryCategory).IsEqualTo(expected);
        await Assert.That(descriptor.Verb.RetryCategory).IsEqualTo(expected);
        await Assert.That(new Verb(name).RetryCategory).IsEqualTo(expected);
    }

    [Test]
    public async Task EveryDescriptorAndBuiltInVerbHasAnIndependentDefinedCategory()
    {
        var catalog = RespireCommands.All.ToArray().ToDictionary(command => command.Name);
        foreach (var descriptor in catalog.Values)
        {
            await Assert.That(Enum.IsDefined(descriptor.RetryCategory)).IsTrue();
            await Assert.That(CommandRetryCategoryMetadata.Get(descriptor.Name.ToLowerInvariant()))
                .IsEqualTo(descriptor.RetryCategory);
        }

        foreach (var field in typeof(Verbs).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(Verb)) continue;
            var verb = (Verb)field.GetValue(null)!;
            var words = Encoding.ASCII.GetString(verb.Bulk).Split("\r\n")
                .Where((_, index) => (index & 1) != 0);
            var name = string.Join(' ', words);
            // Fixed-option verbs absent from the catalog have no execution-specific audit.
            var expected = catalog.TryGetValue(name, out var descriptor)
                ? descriptor.RetryCategory : RespireCommandRetryCategory.Never;
            await Assert.That(verb.RetryCategory).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CallerSuppliedAndDefaultCommandsCannotAcquireRetryPermission()
    {
        await Assert.That(default(RespireCommand).RetryCategory).IsEqualTo(RespireCommandRetryCategory.Never);
        await Assert.That(default(Verb).RetryCategory).IsEqualTo(RespireCommandRetryCategory.Never);
        foreach (var name in new[] { "GET", "SET", "INCR", "PING", "CUSTOM.GET" })
        {
            RespireCommand implicitCommand = name;
            foreach (var descriptor in new[]
            {
                implicitCommand, RespireCommand.Create(name),
                RespireCommand.Create(name.ToLowerInvariant(), RespireCacheMutation.ReadOnly),
                new RespireCommand(name, RespireCommandSource.None, isReadOnly: true,
                    retryCategory: RespireCommandRetryCategory.Always),
            })
            {
                await Assert.That(descriptor.RetryCategory).IsEqualTo(RespireCommandRetryCategory.Never);
                await Assert.That(descriptor.Verb.RetryCategory).IsEqualTo(RespireCommandRetryCategory.Never);
            }
        }
        await Assert.That(new Verb("CUSTOM.GET").RetryCategory).IsEqualTo(RespireCommandRetryCategory.Never);
        await Assert.That(CommandRetryCategoryMetadata.Get("CUSTOM_READONLY"))
            .IsEqualTo(RespireCommandRetryCategory.Never);
    }

    [Test]
    public async Task RetryRiskDoesNotChangeReadRoutingCacheEffectsOrFootprints()
    {
        await Assert.That(RespireCommands.Scripting.EVAL_RO.IsReadOnly).IsTrue();
        await Assert.That(RespireCommands.Scripting.EVAL_RO.ReadKind).IsEqualTo(ReadCommandKind.Read);
        await Assert.That(RespireCommands.Search.FT_CURSOR_READ.CacheMutation)
            .IsEqualTo(RespireCacheMutation.ReadOnly);
        await Assert.That(RespireCommands.Json.JSON_GET.IsReadOnly).IsFalse();
        // Baseline measured on the 64-bit runtime before adding retry metadata.
        await Assert.That(Unsafe.SizeOf<Verb>()).IsEqualTo(IntPtr.Size == 8 ? 32 : 24);
        await Assert.That(Unsafe.SizeOf<RespireCommand>())
            .IsEqualTo(Unsafe.SizeOf<Verb>() + IntPtr.Size + 2 * sizeof(int));
    }

    [Test]
    [NotInParallel]
    public async Task CachedCategoryAccessAllocatesNothingAfterInitialization()
    {
        var command = RespireCommands.String.GET;
        var verb = Verbs.Incr;
        _ = MeasureCategories(in command, in verb, false);
        _ = MeasureCategories(in command, in verb, true);
        var result = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Normal: MeasureCategories(in command, in verb, false),
             Control: MeasureCategories(in command, in verb, true)));
        await Assert.That(result.Normal.Bytes).IsEqualTo(0);
        await Assert.That(result.Control.Bytes).IsGreaterThanOrEqualTo(37_000);
        await Assert.That(result.Normal.Total).IsEqualTo(9_000);
        await Assert.That(result.Control.Total).IsEqualTo(9_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Bytes, int Total) MeasureCategories(in RespireCommand command, in Verb verb, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var index = 0; index < 1_000; index++)
        {
            total += (int)command.RetryCategory + (int)verb.RetryCategory;
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, total);
    }
}
