using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AvailabilityTests
{
    [Test]
    public async Task ConnectAnyAsync_SkipsUnavailableFirstEndpoint_AndUsesLaterCandidateOptions()
    {
        var unavailablePort = GetUnusedLoopbackPort();
        var serializer = new RecordingSerializer();
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);

        await using var client = await RespireClient.ConnectAnyAsync([
            new RespireOptions
            {
                Endpoints = { new RespireEndpoint("127.0.0.1", unavailablePort) },
                ConnectTimeout = TimeSpan.FromMilliseconds(100),
            },
            new RespireOptions
            {
                Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
                Password = "secret",
                ClientName = "respire-availability",
                Serializer = serializer,
                Connections = 1,
            },
        ]);

        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));

        await Assert.That(await client.SetAsync("availability:key", new Payload(42))).IsTrue();
        await Assert.That(serializer.SerializeCalls).IsEqualTo(1);
        var commands = server.ReceivedCommands;
        await Assert.That(commands.Count).IsEqualTo(3);
        await Assert.That(commands[0]).IsEqualTo("AUTH secret");
        await Assert.That(commands[1]).IsEqualTo("CLIENT SETNAME respire-availability");
        await Assert.That(commands[2]).IsEqualTo("SET availability:key selected");
    }

    [Test]
    public async Task ConnectAnyAsync_AllCandidatesFail_ReportsEveryAttempt()
    {
        await using var first = new FakeRespServer("-ERR primary unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR secondary unavailable\r\n"u8.ToArray());

        var exception = await Assert.That(async () => await RespireClient.ConnectAnyAsync([
                new RespireOptions
                {
                    Endpoints = { new RespireEndpoint("127.0.0.1", first.Port) },
                    Password = "wrong",
                },
                new RespireOptions
                {
                    Endpoints = { new RespireEndpoint("127.0.0.1", second.Port) },
                    Password = "wrong",
                },
            ]))
            .ThrowsExactly<RespireConnectionException>();

        await Assert.That(exception!.Message).Contains("Unable to connect to any Redis endpoint candidate");
        await Assert.That(exception.Message).Contains($"1. 127.0.0.1:{first.Port}");
        await Assert.That(exception.Message).Contains("primary unavailable");
        await Assert.That(exception.Message).Contains($"2. 127.0.0.1:{second.Port}");
        await Assert.That(exception.Message).Contains("secondary unavailable");

        var aggregate = exception.InnerException as AggregateException;
        await Assert.That(aggregate).IsNotNull();
        await Assert.That(aggregate!.InnerExceptions.Count).IsEqualTo(2);
    }

    private static int GetUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record Payload(int Value);

    private sealed class RecordingSerializer : IRespireSerializer
    {
        public int SerializeCalls { get; private set; }

        public void Serialize<T>(IBufferWriter<byte> destination, T value)
        {
            SerializeCalls++;
            destination.Write("selected"u8);
        }

        public T? Deserialize<T>(ReadOnlySpan<byte> payload) => default;
    }
}
