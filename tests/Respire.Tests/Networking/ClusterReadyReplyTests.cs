using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ClusterReadyReplyTests
{
    [Test]
    [Arguments("string", false)]
    [Arguments("string", true)]
    [Arguments("bytes", false)]
    [Arguments("bytes", true)]
    [Arguments("integer", false)]
    [Arguments("integer", true)]
    public async Task ReadyPrimaryUsesTypedInflightSource(string shape, bool reconnectPolicy)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = Primary();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ReconnectPolicy = reconnectPolicy ? new() : null,
        });
        var operation = shape == "integer" ? "STRLEN" : "GET";
        var connection = client.Core.Cluster!.TryAcquireReadyConnection(ClusterHash.GetSlot("ready"), default)!;
        await Assert.That(connection).IsNotNull();
        server.SuppressReply = command => command.StartsWith(operation + " ", StringComparison.Ordinal);
        switch (shape)
        {
            case "string":
                await Verify(client.Strings.GetStringAsync("ready"), nameof(StringPendingResponseSource),
                    "$5\r\nvalue\r\n"u8.ToArray(), static value => value == "value");
                break;
            case "bytes":
                await Verify(client.Strings.GetBytesAsync("ready"), nameof(BytesPendingResponseSource),
                    "$5\r\nvalue\r\n"u8.ToArray(), static value => value is not null && value.AsSpan().SequenceEqual("value"u8));
                break;
            case "integer":
                await Verify(client.Strings.LengthAsync("ready"), "ConvertedPendingResponseSource",
                    ":5\r\n"u8.ToArray(), static value => value == 5);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }

        async Task Verify<T>(ValueTask<T> response, string sourceName, byte[] reply, Func<T, bool> valid)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!server.ReceivedCommands.Any(command => command.StartsWith(operation + " ", StringComparison.Ordinal)))
                await Task.Delay(1, timeout.Token);
            var index = Enumerable.Range(0, server.ReceivedCommands.Count).Single(i =>
                server.ReceivedCommands[i].StartsWith(operation + " ", StringComparison.Ordinal));
            try
            {
                // Admission is quiescent and the only response remains suppressed.
                await Assert.That(connection.InspectForTests().Inflight.TryPeek(out var pending)).IsTrue();
                // The normal HTML reporter enables tracing. Its lane must retain the full
                // instrumented path; CI's reporter-disabled lane verifies specialized sources.
                if (RespireTelemetry.IsOperationEnabled(operation)) sourceName = "PendingResponseSource";
                await Assert.That(pending!.GetType().Name.Split('`')[0]).IsEqualTo(sourceName);
            }
            finally
            {
                await server.SendRawAsync(reply, server.ReceivedConnectionIds[index]);
                await Assert.That(valid(await response)).IsTrue();
            }
        }
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    [Arguments("READONLY")]
    [Arguments("retired")]
    public async Task ConverterFailuresNeverTriggerTransportReplay(string code)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = Primary();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var slot = ClusterHash.GetSlot("ready");
        Exception expected = code == "retired"
            ? new RespireConnectionRetiredException("127.0.0.1", server.Port)
            : new RespireServerException($"{code} {slot} 127.0.0.1:{server.Port}");
        var topologyReads = server.ReceivedCommands.Count(command => command == "CLUSTER SLOTS");
        var conversions = 0;
        Exception? actual = null;
        try
        {
            await client.ConvertResponseAsync<Cmd1, Exception, long>("GET", new(Verbs.Get, "ready"), timeout.Token,
                expected, (Exception error, in RespValue _) =>
                {
                    conversions++;
                    throw error;
                });
        }
        catch (Exception error) { actual = error; }

        await Assert.That(actual).IsSameReferenceAs(expected);
        await Assert.That(conversions).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET ready")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(topologyReads);
    }

    [Test]
    [Arguments("string", "MOVED")]
    [Arguments("bytes", "MOVED")]
    [Arguments("integer", "MOVED")]
    [Arguments("string", "ASK")]
    [Arguments("bytes", "ASK")]
    [Arguments("integer", "ASK")]
    [Arguments("string", "READONLY")]
    [Arguments("bytes", "READONLY")]
    [Arguments("integer", "READONLY")]
    public async Task WireRejectionsResumeExistingClusterRecovery(string shape, string code)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var source = Primary();
        await using var target = Primary();
        var operation = shape == "integer" ? "STRLEN" : "GET";
        var slot = ClusterHash.GetSlot("ready");
        var topology = Slots(source.Port);
        source.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") return topology;
            if (command != operation + " ready") return null;
            if (code != "ASK") topology = Slots(target.Port);
            return Encoding.ASCII.GetBytes($"-{code} {slot} 127.0.0.1:{target.Port}\r\n");
        };
        await using var client = await RespireClient.ConnectAsync(Options(source));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        switch (shape)
        {
            case "string":
                await Assert.That(await client.Strings.GetStringAsync("ready", timeout.Token)).IsEqualTo("value");
                break;
            case "bytes":
                await Assert.That((await client.Strings.GetBytesAsync("ready", timeout.Token))!.AsSpan().SequenceEqual("value"u8)).IsTrue();
                break;
            case "integer":
                await Assert.That(await client.Strings.LengthAsync("ready", timeout.Token)).IsEqualTo(5L);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }

        await Assert.That(source.ReceivedCommands.Count(command => command == operation + " ready")).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command == operation + " ready")).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command == "ASKING")).IsEqualTo(code == "ASK" ? 1 : 0);
        await Assert.That(client.Core.Cluster!.GetKnownSlotOwner(slot)!.Port)
            .IsEqualTo(code == "ASK" ? source.Port : target.Port);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task PreAdmissionRetirementResumesOnReplacement(string shape)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var source = Primary();
        await using var target = Primary();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var admissions = 0;
        var command = new AdmissionCommand(shape == "integer" ? Verbs.StrLen : Verbs.Get, () =>
        {
            if (++admissions != 1) return;
            source.ReplyOverride = (_, received) => received == "CLUSTER SLOTS" ? Slots(target.Port) : null;
            var router = client.Core.Cluster!;
            router.SetSlotOwner(ClusterHash.GetSlot("ready"), router.GetMultiplexer(new("127.0.0.1", target.Port)));
            throw new RespireConnectionRetiredException("127.0.0.1", source.Port);
        });
        switch (shape)
        {
            case "string":
                await Assert.That(await client.StringOrNullAsync("GET", command, timeout.Token)).IsEqualTo("value");
                break;
            case "bytes":
                await Assert.That((await client.BytesOrNullAsync("GET", command, timeout.Token))!.AsSpan().SequenceEqual("value"u8)).IsTrue();
                break;
            case "integer":
                await Assert.That(await client.IntegerAsync("STRLEN", command, timeout.Token)).IsEqualTo(5L);
                break;
        }
        var operation = shape == "integer" ? "STRLEN ready" : "GET ready";
        await Assert.That(source.ReceivedCommands.Count(received => received == operation)).IsEqualTo(0);
        await Assert.That(target.ReceivedCommands.Count(received => received == operation)).IsEqualTo(1);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task CancellationBeforeAdmissionReturnsFaultedValueTask(string shape)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = Primary();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Calling must return a task; a synchronous throw fails before the assertion.
        var pending = shape switch
        {
            "string" => client.Strings.GetStringAsync("ready", cancellation.Token).AsTask(),
            "bytes" => (Task)client.Strings.GetBytesAsync("ready", cancellation.Token).AsTask(),
            _ => client.Strings.LengthAsync("ready", cancellation.Token).AsTask(),
        };
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.EndsWith(" ready", StringComparison.Ordinal))).IsFalse();
    }

    private readonly struct AdmissionCommand(Verb verb, Action admit) : IRespCommand
    {
        public ReadCommandKind ReadKind => verb.ReadKind;
        public void ValidateAdmission() => admit();
        public bool TryGetClusterSlot(out int slot)
        {
            slot = ClusterHash.GetSlot("ready");
            return true;
        }
        public void Write(ref RespWriter writer) => new Cmd1(verb, "ready").Write(ref writer);
    }

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], UseCluster = true,
        Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
    };

    private static FakeRespServer Primary()
    {
        var server = new FakeRespServer(8, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Slots(server.Port),
            "GET ready" => "$5\r\nvalue\r\n"u8.ToArray(),
            "STRLEN ready" => ":5\r\n"u8.ToArray(),
            "PING" => FakeRespServer.PongReply,
            _ => null,
        };
        return server;
    }

    private static byte[] Slots(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
}
