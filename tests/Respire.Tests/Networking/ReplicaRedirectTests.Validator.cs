using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

// Each wire fixture owns its fake servers and client to isolate redirect policy and credentials.
#pragma warning disable RESP010
public partial class ReplicaRedirectTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ValidatorRejectsBeforeTargetConnectionOrAuthentication(bool retry, bool script)
    {
        await using var target = new FakeRespServer();
        await using var source = new FakeRespServer
        {
            ReplyOverride = (_, command) => IsApplication(command)
                ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : FakeRespServer.OkReply,
        };
        var destinations = new List<RespireEndpoint>();
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            Username = "user", Password = "secret",
            RetryPolicy = retry ? new() { RetryableErrors = RespireRetryableErrors.ConnectionLoss } : null,
            ReplicaRedirectValidator = endpoint => { destinations.Add(endpoint); return false; },
        });
        var pools = client.Core.OwnedPools.Count;
        var procedure = RespireScript.Create("return 7").WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        var error = await Assert.That(async () =>
        {
            if (script) await client.Scripts.ExecuteIntegerAsync(procedure, "key");
            else await client.GetStringAsync("key");
        }).ThrowsExactly<RespireException>();
        await Assert.That(error!.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", target.Port));
        await Assert.That(destinations).IsEquivalentTo([new RespireEndpoint("127.0.0.1", target.Port)]);
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
        await Assert.That(target.ReceivedCommands).IsEmpty();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        await Assert.That(source.ReceivedCommands.Count(IsApplication)).IsEqualTo(1);
    }

    [Test]
    [Arguments("primary.example", 6379, true)]
    [Arguments("other.example", 6379, false)]
    [Arguments("primary.example", 6380, false)]
    [Arguments("[::1]", 6379, true)]
    [Arguments("::1", 6379, true)]
    [Arguments("[2001:db8::1]", 6379, false)]
    [Arguments("", 6379, true)]
    public async Task ValidatorChecksParsedHostAndPortBeforeAddressMapping(string host, int port, bool allowed)
    {
        var expectedHost = host.Length == 0 ? "127.0.0.1" : host.Trim('[', ']');
        await using var target = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "GET key" ? Bytes("$5\r\nvalue\r\n") : FakeRespServer.OkReply,
        };
        await using var source = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "GET key"
                ? Bytes($"-REDIRECT {host}:{port}\r\n") : FakeRespServer.OkReply,
        };
        var validated = new List<RespireEndpoint>();
        var mapped = new List<RespireEndpoint>();
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            ReplicaRedirectValidator = endpoint =>
            {
                validated.Add(endpoint);
                return endpoint.Port == 6379 && endpoint.Host is "primary.example" or "::1" or "127.0.0.1";
            },
            NodeAddressResolver = endpoint =>
            {
                mapped.Add(endpoint);
                return endpoint.Port == source.Port ? endpoint : new("127.0.0.1", target.Port);
            },
        });
        if (allowed) await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        else await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireException>();
        await Assert.That(validated).IsEquivalentTo([new RespireEndpoint(expectedHost, port)]);
        await Assert.That(mapped.Contains(new(expectedHost, port))).IsEqualTo(allowed);
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsEqualTo(allowed);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    public async Task ValidatorAppliesToEveryHop(bool retry, bool script, bool allowFinal)
    {
        await using var final = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "GET key" ? Bytes("$1\r\n7\r\n")
                : IsApplication(command) ? Bytes(":7\r\n") : FakeRespServer.OkReply,
        };
        await using var intermediate = new FakeRespServer
        {
            ReplyOverride = (_, command) => IsApplication(command)
                ? Bytes($"-REDIRECT 127.0.0.1:{final.Port}\r\n") : FakeRespServer.OkReply,
        };
        await using var source = new FakeRespServer
        {
            ReplyOverride = (_, command) => IsApplication(command)
                ? Bytes($"-REDIRECT 127.0.0.1:{intermediate.Port}\r\n") : FakeRespServer.OkReply,
        };
        var destinations = new List<RespireEndpoint>();
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            Password = "secret", RetryPolicy = retry ? new() : null,
            ReplicaRedirectValidator = endpoint =>
            {
                destinations.Add(endpoint);
                return endpoint.Host == "127.0.0.1" && (endpoint.Port == intermediate.Port || allowFinal);
            },
        });
        var pools = client.Core.OwnedPools.Count;
        async Task Send()
        {
            if (script)
                await Assert.That(await client.Scripts.ExecuteIntegerAsync(
                    RespireScript.Create("return 7").WithRetryCategory(RespireCommandRetryCategory.ReadOnly), "key")).IsEqualTo(7);
            else await Assert.That(await client.GetStringAsync("key")).IsEqualTo("7");
        }
        if (allowFinal) await Send();
        else await Assert.That(Send).ThrowsExactly<RespireException>();
        await Assert.That(destinations).IsEquivalentTo([
            new RespireEndpoint("127.0.0.1", intermediate.Port), new RespireEndpoint("127.0.0.1", final.Port)]);
        await Assert.That(intermediate.ReceivedCommands.Contains("AUTH secret")).IsTrue();
        await Assert.That(final.ConnectionAccepted.IsCompleted).IsEqualTo(allowFinal);
        await Assert.That(final.ReceivedCommands.Contains("AUTH secret")).IsEqualTo(allowFinal);
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidatorFailureIsTerminalAndPreservesCause(bool retry)
    {
        await using var target = new FakeRespServer();
        await using var source = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "GET key"
                ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : FakeRespServer.OkReply,
        };
        var cause = new RespireConnectionException("Policy unavailable");
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            RetryPolicy = retry ? new() { RetryableErrors = RespireRetryableErrors.ConnectionLoss } : null,
            ReplicaRedirectValidator = _ => throw cause,
        });
        var error = await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireException>();
        await Assert.That(error!.InnerException).IsEqualTo(cause);
        await Assert.That(error.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", target.Port));
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ValidatorDoesNotEnableReplicaRedirects()
    {
        await using var target = new FakeRespServer();
        await using var source = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "GET key"
                ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : FakeRespServer.OkReply,
        };
        var calls = 0;
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            EnableReplicaRedirects = false,
            ReplicaRedirectValidator = _ => { calls++; return true; },
        });
        var error = await Assert.That(async () => await client.GetStringAsync("key")).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("REDIRECT");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ValidatorRejectsTrackedScriptFallbackHop()
    {
        await using var final = TrackedTarget();
        await using var intermediate = TrackedTarget();
        var normal = intermediate.ReplyOverride!;
        intermediate.ReplyOverride = (id, command) => command.StartsWith("EVAL ", StringComparison.Ordinal)
            ? Bytes($"-REDIRECT 127.0.0.1:{final.Port}\r\n") : normal(id, command);
        await using var source = CorrectionReplica(intermediate.Port, lua: false);
        var destinations = new List<RespireEndpoint>();
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            ReplicaRedirectValidator = endpoint =>
            {
                destinations.Add(endpoint);
                return endpoint.Port == intermediate.Port;
            },
        });
        await client.EnsureReliableCorrectionOrderingAsync();
        var pools = client.Core.OwnedPools.Count;
        var execution = await client.StartTrackedScriptExecutionAsync(RespireScript.Create("return 1"), ["key"], [], default,
            requireReliableCorrectionOrdering: true);
        var error = await Assert.That(async () => { using var reply = await execution.ConsumeResponseAsync(); })
            .ThrowsExactly<RespireException>();
        await Assert.That(error!.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", final.Port));
        await Assert.That(destinations).IsEquivalentTo([
            new RespireEndpoint("127.0.0.1", intermediate.Port), new RespireEndpoint("127.0.0.1", final.Port)]);
        await Assert.That(final.ConnectionAccepted.IsCompleted).IsFalse();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
    }

    private static bool IsApplication(string command)
        => command == "GET key" || command.StartsWith("EVAL", StringComparison.Ordinal);
}
