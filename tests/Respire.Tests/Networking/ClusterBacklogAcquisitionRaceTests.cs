using System.Net.Sockets;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterBacklogAcquisitionRaceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments("tracked")]
    [Arguments("stream")]
    [Arguments("hedged")]
    [Arguments("closing")]
    public async Task InitialAcquisitionFailureJoinsBacklogBeforeSending(string surface)
    {
        await using var fixture = await Fixture.CreateAsync(surface);
        fixture.ArmAcquisitionRace();
        var pending = ExecuteAsync(fixture.Client, surface);
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(fixture.RaceInjected).IsTrue();
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Contains(ApplicationCommand(surface))).IsFalse();
        fixture.Resume.TrySetResult();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo(surface == "closing" ? "OK" : "value");
        // A later reply confirms the server has observed the fire-and-forget command too.
        await fixture.Client.GetStringAsync("{race}barrier");
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == ApplicationCommand(surface)))
            .IsEqualTo(1);
    }

    private static string ApplicationCommand(string surface)
        => surface == "closing" ? "SHUTDOWN NOSAVE" : "GET {race}key";

    private static async Task<string> ExecuteAsync(RespireClient client, string surface)
    {
        if (surface == "closing")
        {
            await client.ExecuteFireAndForgetAsync("SHUTDOWN", "NOSAVE");
            return "OK";
        }
        if (surface == "stream")
        {
            await using var stream = await client.Strings.GetStreamAsync("{race}key");
            using var reader = new StreamReader(stream!);
            return await reader.ReadToEndAsync();
        }
        return (await client.GetStringAsync("{race}key"))!;
    }

    private sealed class Fixture(FakeRespServer server, RespireClient client,
        TaskCompletionSource connecting, TaskCompletionSource resume, Action enableGate) : IAsyncDisposable
    {
        internal FakeRespServer Server { get; } = server;
        internal RespireClient Client { get; } = client;
        internal TaskCompletionSource Connecting { get; } = connecting;
        internal TaskCompletionSource Resume { get; } = resume;
        internal bool RaceInjected { get; private set; }

        internal void ArmAcquisitionRace()
        {
            var router = Client.Core.Cluster!;
            var connection = router.GetMultiplexer(new("127.0.0.1", Server.Port)).GetConnection();
            router.BacklogAdmissionCheckedForTests = shouldBacklog =>
            {
                if (shouldBacklog) throw new InvalidOperationException("The precheck must first see a connected route.");
                router.BacklogAdmissionCheckedForTests = null;
                enableGate();
#pragma warning disable CA1849, RESP009 // Synchronously inject disconnect between the precheck snapshot and initial acquisition.
                connection.DisposeAsync().GetAwaiter().GetResult();
#pragma warning restore CA1849, RESP009
                RaceInjected = true;
            };
        }

        internal static async Task<Fixture> CreateAsync(string surface)
        {
            var server = new FakeRespServer(8, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command switch
                {
                    "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                    "HELLO 3 AUTH default test" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                    _ => command.StartsWith("GET ", StringComparison.Ordinal) ? "$5\r\nvalue\r\n"u8.ToArray() : null,
                },
            };
            try
            {
                var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var gateReplacement = false;
#pragma warning disable RESP010 // Each test owns an isolated transport and a deterministic disconnect gate.
                var client = await RespireClient.ConnectAsync(new RespireOptions
                {
                    UseCluster = true, AllowAdmin = true, Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
                    Protocol = surface == "tracked" ? RespProtocol.Resp3 : RespProtocol.Resp2, Password = "test",
                    CommandTimeout = Limit, ConnectTimeout = Limit, RetryPolicy = null,
                    ClientSideCache = surface == "tracked" ? new() : null,
                    ReadFrom = surface == "hedged" ? RespireReadFrom.PrimaryPreferred : RespireReadFrom.Primary,
                    HedgedReads = surface == "hedged" ? new() : null,
                    ClusterTopologyRefreshInterval = null,
                    TestingStreamFactory = async (host, port, token) =>
                    {
                        if (Volatile.Read(ref gateReplacement))
                        {
                            connecting.TrySetResult();
                            await resume.Task.WaitAsync(token);
                        }
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(host, port, token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    },
                });
#pragma warning restore RESP010
                var router = client.Core.Cluster!;
                router.SetSlotOwner(ClusterHash.GetSlot("{race}key"), router.GetMultiplexer(new("127.0.0.1", server.Port)));
                return new(server, client, connecting, resume, () => Volatile.Write(ref gateReplacement, true));
            }
            catch { await server.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Core.Cluster!.BacklogAdmissionCheckedForTests = null;
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
