using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class CommandConversionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapacityWaitOwnsCommandBeforeCallerReassignsIt(bool converted)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(":7\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "STRLEN parked") return false;
                arrived.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 1,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            CommandTimeout = null,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var parked = client.Strings.LengthAsync("parked", timeout.Token).AsTask();
        await arrived.Task.WaitAsync(timeout.Token);

        var command = new Cmd3(Verbs.SetRange, "original", 0, "payload");
        var response = converted
            ? client.IntegerAsync("SETRANGE", in command, timeout.Token)
            : ReadInteger(client.SendAsync("SETRANGE", in command, timeout.Token));
        command = new Cmd3(Verbs.SetRange, "changed", 9, "replacement");
        // The full ring holds serialization until the caller's variable has changed.
        await Assert.That(response.IsCompleted).IsFalse();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["STRLEN parked"]);
        await server.SendRawAsync(":1\r\n"u8.ToArray());
        await Assert.That(await parked.WaitAsync(timeout.Token)).IsEqualTo(1L);
        await Assert.That(await response.AsTask().WaitAsync(timeout.Token)).IsEqualTo(7L);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["STRLEN parked", "SETRANGE original 0 payload"]);
        await Assert.That(command.TryGetPrimaryKey(out var key)).IsTrue();
        await Assert.That(key.ToString()).IsEqualTo("changed");
    }

    private static async ValueTask<long> ReadInteger(ValueTask<RespValue> pending)
    {
        using var response = await pending;
        return response.AsInteger();
    }
}
