using System.Text;
using Microsoft.Extensions.Logging;
using Respire.OutputCaching;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class OutputCachePipelineTests
{
    [Test]
    [Arguments(2)]
    [Arguments(126)]
    public async Task RegistrationPipelinesTagsBeforePublishing(int tagCount)
    {
        var firstGroup = Math.Min(tagCount, 125) * 2;
        await using var server = new FakeRespServer { MinimumCommandsBeforeReply = firstGroup };
        var registrations = 0;
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("EVAL ", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref registrations) == firstGroup) server.MinimumCommandsBeforeReply = 1;
                return ":1\r\n"u8.ToArray();
            }
            return FakeRespServer.OkReply;
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var store = new RespireOutputCacheStore(client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.SetAsync("key", [1], Enumerable.Range(0, tagCount).Select(index => $"tag{index}").ToArray(), TimeSpan.FromMinutes(1), deadline.Token);
        await Assert.That(registrations).IsEqualTo(tagCount * 2);
        await Assert.That(server.ReceivedCommands.Last()).StartsWith("SET __MSOCV_key ");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CleanupPipelinesRemovalsAndReportsLockLoss(bool loseLock)
    {
        var count = loseLock ? 250 : 3;
        var removed = 0;
        var logger = new CapturingLogger();
        await using var server = new FakeRespServer();
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("ZSCAN ", StringComparison.Ordinal)) return ScanReply(count);
            if (command.StartsWith("ZREMRANGEBYSCORE __MSOCT_tag", StringComparison.Ordinal))
            {
                var current = Interlocked.Increment(ref removed);
                // Do not reply to the first removal until the entire group arrives.
                server.MinimumCommandsBeforeReply = current == count ? 1 : count;
                return ":1\r\n"u8.ToArray();
            }
            if (command.StartsWith("SET ", StringComparison.Ordinal))
                return command.Contains(" IFEQ ", StringComparison.Ordinal) ? "$-1\r\n"u8.ToArray() : FakeRespServer.OkReply;
            if (command.StartsWith("EVAL", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
            return ":1\r\n"u8.ToArray();
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var store = new RespireOutputCacheStore(client, logger: logger);
        await store.CollectExpiredTagsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(removed).IsEqualTo(count);
        await Assert.That(logger.Messages.Any(message => message.Contains("lost its lock", StringComparison.Ordinal))).IsEqualTo(loseLock);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("ZREMRANGEBYSCORE __MSOCT ", StringComparison.Ordinal)))
            .IsEqualTo(!loseLock);
    }

    [Test]
    public async Task DirectConstructionRejectsMissingClockWithoutDiValidation()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(() => new RespireOutputCacheStore(client, new() { TimeProvider = null! }))
            .ThrowsExactly<ArgumentNullException>();
    }

    private static byte[] ScanReply(int count)
    {
        var reply = new StringBuilder($"*2\r\n$1\r\n0\r\n*{count * 2}\r\n");
        for (var index = 0; index < count; index++)
        {
            var tag = $"tag{index}";
            reply.Append('$').Append(tag.Length).Append("\r\n").Append(tag).Append("\r\n$1\r\n1\r\n");
        }
        return Encoding.UTF8.GetBytes(reply.ToString());
    }

    private sealed class CapturingLogger : ILogger<RespireOutputCacheStore>
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
