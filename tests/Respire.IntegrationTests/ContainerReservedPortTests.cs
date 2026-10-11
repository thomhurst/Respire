using System.Globalization;
using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ContainerReservedPortTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Standalone)]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Cluster)]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Sentinel)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Standalone)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Cluster)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Sentinel)]
    public async Task OutgoingConnectionCannotTakePortBeforeDaemonBinds(
        RespireContainerServer server, RespireContainerTopology topology)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // The one-port ephemeral range makes the original race deterministic. Docker
        // applies it inside each container's network namespace, never on the host.
        const int port = 49153;
        var number = port.ToString(CultureInfo.InvariantCulture);
        var options = new RespireContainerOptions { Server = server, Topology = topology };
        var binary = server == RespireContainerServer.Redis ? "redis" : "valkey";

        foreach (var reservePort in new[] { false, true })
        {
            await using var container = RespireContainerFixture.BuildContainer(options, [port], builder => builder
                .WithPortBinding(port, assignRandomHostPort: true)
                .WithCreateParameterModifier(parameters =>
                {
                    var hostConfig = parameters.HostConfig!;
                    hostConfig.Sysctls ??= new Dictionary<string, string>();
                    hostConfig.Sysctls["net.ipv4.ip_local_port_range"] = $"{number} {number}";
                    // Positive control: without the reservation, the automatic source
                    // port allocation claims the future daemon's listening port.
                    if (!reservePort) hostConfig.Sysctls["net.ipv4.ip_local_reserved_ports"] = "";
                }));
            await container.StartAsync(deadline.Token);
            var start = await container.ExecAsync(
                [$"{binary}-server", "--port", "12345", "--daemonize", "yes", "--save", "", "--appendonly", "no"], deadline.Token);
            start.ExitCode.Should().Be(0, start.Stdout + start.Stderr);
            // No explicit source port: this exercises the kernel's automatic allocator.
            var connect = await container.ExecAsync([$"{binary}-cli", "-p", "12345", "PING"], deadline.Token);
            if (reservePort)
            {
                connect.ExitCode.Should().NotBe(0);
                (connect.Stdout + connect.Stderr).Should().Contain("Address not available");
            }
            else
            {
                connect.ExitCode.Should().Be(0, connect.Stdout + connect.Stderr);
                connect.Stdout.Trim().Should().Be("PONG");
                // /proc records the allocated source port, including TIME_WAIT after
                // redis-cli exits. Inspect before any topology daemon starts listening.
                var sockets = await container.ExecAsync(["cat", "/proc/net/tcp"], deadline.Token);
                sockets.ExitCode.Should().Be(0);
                sockets.Stdout.Should().Contain($"0100007F:{port:X4} 0100007F:3039");
            }
        }
    }
}
