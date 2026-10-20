using System.Reflection;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class NotificationWaitConfigurationTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task NoLoopIsRejectedBeforeSendingWaitCommands(bool hashFieldLease, bool prefixedView, bool decorated)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                var value when value.StartsWith("CLIENT ", StringComparison.Ordinal) => FakeRespServer.OkReply,
                _ => "-ERR unexpected coordination command\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new() { NoLoop = true },
        });
        var commandsBeforeWait = server.CommandsSeen;
        IRespireClient waitClient = prefixedView ? client.WithKeyPrefix("tenant:") : client;
        if (decorated)
        {
            var proxy = DispatchProxy.Create<IRespireClient, ForwardingClient>();
            ((ForwardingClient)proxy).Inner = waitClient;
            waitClient = proxy;
        }
        var coordination = new RespireCoordination(waitClient);

        await Assert.That(async () =>
        {
            if (hashFieldLease)
            {
                await using var lease = await coordination.AcquireLeaseAsync(
                    "leases", "worker", TimeSpan.FromMinutes(1));
            }
            else
            {
                await using var lease = await coordination.AcquireFencedLockAsync(
                    "lease", "counter", TimeSpan.FromMinutes(1));
            }
        }).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(commandsBeforeWait);
    }

    public class ForwardingClient : DispatchProxy
    {
        public IRespireClient Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Invoke(Inner, args);
    }
}
