using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCacheQueryDependencyWireTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RedirectAfterMutationRetirementDoesNotPublishUntrackedReply(bool moved, bool coalesce)
    {
        var reads = 0;
        await using var target = new FakeRespServer(100, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "STRLEN key"
                ? System.Text.Encoding.ASCII.GetBytes($":{Interlocked.Increment(ref reads) + 2}\r\n")
                : Handshake(command),
        };
        var slot = Respire.Internal.ClusterHash.GetSlot("key");
        var redirect = System.Text.Encoding.ASCII.GetBytes($"-{(moved ? "MOVED" : "ASK")} {slot} 127.0.0.1:{target.Port}\r\n");
        await using var seed = new FakeRespServer(100, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "STRLEN key",
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
                : command == "STRLEN key" ? redirect : Handshake(command),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("127.0.0.1", seed.Port)], Connections = 1,
            Protocol = RespProtocol.Resp3, ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
            CommandTimeout = Limit, ConnectTimeout = Limit,
        });
        var cache = client.Core.ClientCache!;
        var mutation = cache.BeginUnknownMutation();
        try
        {
            var reading = client.Strings.LengthAsync("key").AsTask();
            await WaitUntilAsync(() => seed.ReceivedCommands.Contains("STRLEN key"));
            cache.CompleteMutation(in mutation);
            seed.SuppressReply = null;
            await seed.SendRawAsync(redirect);
            await Assert.That(await reading.WaitAsync(Limit)).IsEqualTo(3);
            await Assert.That(cache.Count).IsEqualTo(0);
            await Assert.That(target.ReceivedCommands.Contains("CLIENT CACHING YES")).IsFalse();
            await Assert.That(await client.Strings.LengthAsync("key")).IsEqualTo(4);
            await Assert.That(await client.Strings.LengthAsync("key")).IsEqualTo(4);
            await Assert.That(cache.Count).IsEqualTo(1);
            await Assert.That(target.ReceivedCommands.Count(command => command == "STRLEN key")).IsEqualTo(2);
            await Assert.That(target.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(1);
            await Assert.That(Pending(client)).IsEqualTo(0);
        }
        finally { cache.CompleteMutation(in mutation); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PrefixedMissingHashFieldsPublishAcrossUnrelatedInvalidations(bool coalesce, bool affected)
    {
        await using var server = Server();
        server.SuppressReply = command => command.StartsWith("HMGET ", StringComparison.Ordinal);
        await using var client = await ConnectAsync(server, coalesce);
        var view = client.WithKeyPrefix("tenant:");
        var reading = view.Hashes.GetManyAsync("first", "a", "missing").AsTask();
        await WaitUntilAsync(() => server.ReceivedCommands.Contains("HMGET tenant:first a missing"));
        var key = affected ? "tenant:first" : "tenant:other";
        await server.SendRawAsync(System.Text.Encoding.ASCII.GetBytes($">2\r\n+invalidate\r\n*1\r\n${key.Length}\r\n{key}\r\n"));
        await WaitUntilAsync(() => client.ClientSideCache!.GetStatistics().Invalidations == 1);
        await server.SendRawAsync("*2\r\n$1\r\nA\r\n$-1\r\n"u8.ToArray());
        await Assert.That(await reading.WaitAsync(Limit)).IsEquivalentTo(new string?[] { "A", null });
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(affected ? 0 : 2);
        await WaitUntilAsync(() => Pending(client) == 0);
        server.SuppressReply = null;
        await Assert.That(await view.Hashes.GetManyAsync("first", "missing", "a"))
            .IsEquivalentTo(new string?[] { null, "A" });
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("HMGET ", StringComparison.Ordinal)))
            .IsEqualTo(affected ? 2 : 1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CancellationAndDisposalRetirePendingQueryDependencies(bool coalesce, bool hash)
    {
        foreach (var dispose in new[] { false, true })
        {
            await using var server = Server();
            server.SuppressReply = command => command.StartsWith(hash ? "HMGET " : "STRLEN ", StringComparison.Ordinal);
            await using var client = await ConnectAsync(server, coalesce);
            var cache = client.Core.ClientCache!;
            using var cancellation = new CancellationTokenSource();
            var reading = ReadAsync(client, hash, cancellation.Token);
            await WaitUntilAsync(() => Pending(client) == 1);
            if (dispose) await client.DisposeAsync();
            else cancellation.Cancel();
            Exception? failure = null;
            try { await reading.WaitAsync(Limit); }
            catch (Exception error) { failure = error; }
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure is TimeoutException).IsFalse();
            if (!dispose) await Assert.That(failure is OperationCanceledException).IsTrue();
            await WaitUntilAsync(() => ClientCacheQueryDependencyTests.PendingDependencies(cache) == 0);
            await Assert.That(cache.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ServerErrorsAndCallerConversionFailuresRetirePendingQueryDependencies(bool coalesce, bool hash)
    {
        foreach (var reply in new[] { "-ERR rejected\r\n", "+unexpected\r\n" })
        {
            await using var server = Server();
            server.ReplyOverride = (_, command) => command.StartsWith(hash ? "HMGET " : "STRLEN ", StringComparison.Ordinal)
                ? System.Text.Encoding.ASCII.GetBytes(reply) : Handshake(command);
            await using var client = await ConnectAsync(server, coalesce);
            await Assert.That(async () => await ReadAsync(client, hash, default)).Throws<Exception>();
            await WaitUntilAsync(() => Pending(client) == 0);
            // A caller's strict converter can fail after an otherwise cacheable RESP reply was
            // published. The dependency lease must still retire exactly once.
            if (hash || reply.StartsWith("-ERR", StringComparison.Ordinal))
                await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        }
    }

    private static async Task ReadAsync(RespireClient client, bool hash, CancellationToken cancellation)
    {
        using var response = await client.ExecuteAsync((RespireCommand)(hash ? "HMGET" : "STRLEN"),
            hash ? ["first", "a", "missing"] : ["first"], cancellationToken: cancellation);
        if (response.IsError) throw new RespireServerException(response.ErrorMessage);
        if (!hash && response.Type != RespDataType.Integer) throw new FormatException("Expected an integer query reply.");
    }

    private static FakeRespServer Server() => new(FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith("HMGET ", StringComparison.Ordinal)) return Handshake(command);
            return command.Contains("missing a", StringComparison.Ordinal)
                ? "*2\r\n$-1\r\n$1\r\nA\r\n"u8.ToArray() : "*2\r\n$1\r\nA\r\n$-1\r\n"u8.ToArray();
        },
    };

    private static byte[] Handshake(string command) => command switch
    {
        "HELLO 3" => Hello,
        "CLIENT ID" => ":123\r\n"u8.ToArray(),
        _ => FakeRespServer.OkReply,
    };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server, bool coalesce)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = RespProtocol.Resp3,
            ClientSideCache = new() { ReuseHashFields = true, CoalesceConcurrentMisses = coalesce },
            CommandTimeout = Limit, ConnectTimeout = Limit,
        });

    private static int Pending(RespireClient client) => ClientCacheQueryDependencyTests.PendingDependencies(client.Core.ClientCache!);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
