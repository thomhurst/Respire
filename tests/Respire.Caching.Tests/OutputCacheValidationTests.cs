using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Respire.OutputCaching;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class OutputCacheValidationTests
{
    [Test]
    [Arguments("one")]
    [Arguments("two")]
    [Arguments("last")]
    public async Task TagFailureDoesNotPublishTheValue(string failingTag)
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) =>
            {
                if (IsTagRegistration(command, $"output:__MSOCT_{failingTag}"))
                    return "-WRONGTYPE invalid tag index\r\n"u8.ToArray();
                return command.StartsWith("SET ", StringComparison.Ordinal) ? FakeRespServer.OkReply : ":1\r\n"u8.ToArray();
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var store = new RespireOutputCacheStore(client, new() { InstanceName = "output:" });
        string[] tags = failingTag == "last" ? Enumerable.Range(0, 125).Select(index => $"tag{index}").Append("last").ToArray() : ["one", "two"];
        await Assert.That(async () => await store.SetAsync("key", "value"u8.ToArray(), tags, TimeSpan.FromMinutes(1)))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task CancellationDuringTagRegistrationDoesNotPublishTheValue()
    {
        using var cancellation = new CancellationTokenSource();
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) =>
            {
                if (IsTagRegistration(command, "__MSOCT_tag")) cancellation.Cancel();
                return command.StartsWith("SET ", StringComparison.Ordinal) ? FakeRespServer.OkReply : ":1\r\n"u8.ToArray();
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var store = new RespireOutputCacheStore(client);
        await Assert.That(async () => await store.SetAsync("key", "value"u8.ToArray(), ["tag"], TimeSpan.FromMinutes(1), cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task DelayedValueUsesTheDeadlineAlreadyRegisteredForItsTags()
    {
        var clock = new TestClock(2_000_000_000_000);
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) =>
            {
                if (IsTagRegistration(command, "output:__MSOCT_tag")) clock.Advance(10_000);
                return command.StartsWith("SET ", StringComparison.Ordinal) ? FakeRespServer.OkReply : ":1\r\n"u8.ToArray();
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var store = new RespireOutputCacheStore(client, new() { InstanceName = "output:", TimeProvider = clock });
        await store.SetAsync("key", "value"u8.ToArray(), ["tag"], TimeSpan.FromMinutes(1));
        await Assert.That(server.ReceivedCommands.Single(command => IsTagRegistration(command, "output:__MSOCT_tag"))
            .EndsWith(" 1 output:__MSOCT_tag key 2000000060000", StringComparison.Ordinal)).IsTrue();
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("SET output:__MSOCV_key value PXAT 2000000060000");
    }

    [Test]
    [Arguments(0)]
    [Arguments(0.5)]
    [Arguments(-1)]
    [Arguments(4294967295d)]
    public async Task InvalidCleanupIntervalFailsStartupValidation(double milliseconds)
    {
        var services = new ServiceCollection();
        services.AddRespireOutputCache(options => options.CleanupInterval = TimeSpan.FromMilliseconds(milliseconds));
        await using var provider = services.BuildServiceProvider();
        await Assert.That(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .ThrowsExactly<OptionsValidationException>();
    }

    [Test]
    public async Task MissingClockFailsStartupValidation()
    {
        var services = new ServiceCollection();
        services.AddRespireOutputCache(options => options.TimeProvider = null!);
        await using var provider = services.BuildServiceProvider();
        await Assert.That(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .ThrowsExactly<OptionsValidationException>();
    }

    private static bool IsTagRegistration(string command, string tagKey) =>
        command.StartsWith("EVAL ", StringComparison.Ordinal)
        && command.Contains($" 1 {tagKey} ", StringComparison.Ordinal);

    private sealed class TestClock(long milliseconds) : TimeProvider
    {
        private long _milliseconds = milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _milliseconds));
        public void Advance(long milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
    }
}
