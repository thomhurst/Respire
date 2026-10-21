using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class ReplicaRedirectTests
{
    [Test]
    [MatrixDataSource]
    public async Task CachedRedirectValidatesBeforeAcquiringNewOrRetainedTarget(
        [Matrix(false, true)] bool retry, [Matrix(false, true)] bool retained,
        [Matrix(false, true)] bool throws, [Matrix("GET", "MGET", "SCRIPT")] string operation)
    {
        await using var target = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? Bytes("$1\r\n7\r\n") : command.StartsWith("MGET ", StringComparison.Ordinal)
                ? Bytes("*2\r\n$1\r\n7\r\n$1\r\n7\r\n") : IsApplication(command)
                ? Bytes(":7\r\n") : CachedValidatorHandshake(command),
        };
        await using var source = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                || command.StartsWith("MGET ", StringComparison.Ordinal) || IsApplication(command)
                    ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : CachedValidatorHandshake(command),
        };
        var allowed = retained;
        var calls = 0;
        var cause = new RespireConnectionException("Policy unavailable");
#pragma warning disable RESP010 // Each matrix case owns its policy, credentials and retained-target state.
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(), Username = "user", Password = "secret",
            CommandTimeout = TimeSpan.FromSeconds(10),
            RetryPolicy = retry ? new() { RetryableErrors = RespireRetryableErrors.ConnectionLoss } : null,
            ReplicaRedirectValidator = _ =>
            {
                calls++;
                if (allowed) return true;
                if (throws) throw cause;
                return false;
            },
        });
#pragma warning restore RESP010
        if (retained) await Assert.That(await client.GetStringAsync("seed")).IsEqualTo("7");
        allowed = false;
        var commands = target.ReceivedCommands.Count;
        var before = client.ClientSideCache!.GetStatistics().ContinuityFlushes;
        var error = await Assert.That(async () =>
        {
            if (operation == "GET") await client.GetStringAsync("key");
            else if (operation == "MGET") await client.Strings.GetManyAsync("key", "other");
            else await client.Scripts.ExecuteIntegerAsync(
                RespireScript.Create("return 7").WithRetryCategory(RespireCommandRetryCategory.ReadOnly), "key");
        }).ThrowsExactly<RespireException>();
        await Assert.That(error!.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", target.Port));
        await Assert.That(error.IsCommandNotSubmitted).IsTrue();
        await Assert.That(error.InnerException).IsEqualTo(throws ? cause : null);
        await Assert.That(calls).IsEqualTo(retained ? 2 : 1);
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsEqualTo(retained);
        await Assert.That(target.ReceivedCommands.Count).IsEqualTo(commands);
        await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(retained ? 1 : 0);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(1L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CachedRedirectValidatesEveryHopBeforeConnecting(bool retry)
    {
        await using var final = new FakeRespServer(4);
        await using var intermediate = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => command == "GET key"
                ? Bytes($"-REDIRECT 127.0.0.1:{final.Port}\r\n") : CachedValidatorHandshake(command),
        };
        await using var source = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => command == "GET key"
                ? Bytes($"-REDIRECT 127.0.0.1:{intermediate.Port}\r\n") : CachedValidatorHandshake(command),
        };
        var destinations = new List<RespireEndpoint>();
#pragma warning disable RESP010 // This case owns both hop identities and its destination policy.
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(), RetryPolicy = retry ? new() : null,
            ReplicaRedirectValidator = endpoint =>
            {
                destinations.Add(endpoint);
                return endpoint.Port == intermediate.Port;
            },
        });
#pragma warning restore RESP010
        var error = await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireException>();
        await Assert.That(error!.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", final.Port));
        await Assert.That(destinations).IsEquivalentTo([
            new RespireEndpoint("127.0.0.1", intermediate.Port), new RespireEndpoint("127.0.0.1", final.Port)]);
        await Assert.That(final.ConnectionAccepted.IsCompleted).IsFalse();
        await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(1);
    }

    private static byte[] CachedValidatorHandshake(string command)
        => command.StartsWith("HELLO 3", StringComparison.Ordinal)
            ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n") : FakeRespServer.OkReply;
}
