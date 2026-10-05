using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// Uncached GET decoded as a string must use the specialized string source, which decodes
/// small bulk replies straight from the receive buffer. Any telemetry listener (including
/// TUnit's HTML reporter) routes commands through the instrumented path instead, so CI also
/// runs this class with TUNIT_DISABLE_HTML_REPORTER=true.
/// </summary>
public class GetStringDirectPathTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_inflight")]
    private static extern ref InflightRing Inflight(RespireConnection connection);

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GetStringAsync_UsesStringPendingSourceWhenCacheIsOff(bool viaFacet)
    {
        Skip.When(RespireTelemetry.IsEnabled, "A telemetry listener disables the uninstrumented fast path.");
        await using var server = new FakeRespServer { SuppressReply = command => command.StartsWith("GET", StringComparison.Ordinal) };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var connection = client.Core.Multiplexer.GetConnection();
        var commandsBefore = server.CommandsSeen;

        var pending = (viaFacet ? client.Strings.GetStringAsync("key") : client.GetStringAsync("key")).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == commandsBefore)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("GET completed before the fake server received it.");
            }
            try { await Task.Delay(1, timeout.Token); }
            catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
            {
                if (pending.IsCompleted) await pending;
                throw new TimeoutException("The fake server did not receive GET within five seconds.", error);
            }
        }

        await Assert.That(Inflight(connection).TryPeek(out var source)).IsTrue();
        await Assert.That(source).IsTypeOf<StringPendingResponseSource>();

        await server.SendRawAsync("$5\r\nhello\r\n"u8.ToArray());
        await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("hello");
        await Assert.That(server.ReceivedCommands[^1]).IsEqualTo("GET key");
    }
}
