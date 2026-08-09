using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class LockCommandTests
{
    [Test]
    public async Task LockCommands_WriteExpectedFramesAndParseReplies()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nowner\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.TakeAsync("resource", "owner", TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(await client.Locks.QueryAsync("resource")).IsEqualTo("owner");
        await Assert.That(await client.Locks.ExtendAsync("resource", "owner", TimeSpan.FromSeconds(45))).IsTrue();
        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsTrue();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SET resource owner NX PX 30000",
            "GET resource",
            $"EVALSHA {LockCommands.ExtendScript.Sha1} 1 resource owner 45000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 resource owner",
        });
    }

    [Test]
    public async Task LockCommands_ReturnFalseWhenAcquireOrOwnershipCheckFails()
    {
        await using var server = new FakeRespServer(
            "$-1\r\n"u8.ToArray(),
            "$-1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.TakeAsync("resource", "owner", TimeSpan.FromSeconds(30))).IsFalse();
        await Assert.That(await client.Locks.QueryAsync("resource")).IsNull();
        await Assert.That(await client.Locks.ExtendAsync("resource", "owner", TimeSpan.FromSeconds(45))).IsFalse();
        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsFalse();
    }

    [Test]
    public async Task LockCommands_ApplyKeyPrefixesToSetGetAndScripts()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nowner\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");

        await client.Locks.TakeAsync("resource", "owner", TimeSpan.FromSeconds(30));
        await client.Locks.QueryAsync("resource");
        await client.Locks.ExtendAsync("resource", "owner", TimeSpan.FromSeconds(45));
        await client.Locks.ReleaseAsync("resource", "owner");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "SET tenant:resource owner NX PX 30000",
            "GET tenant:resource",
            $"EVALSHA {LockCommands.ExtendScript.Sha1} 1 tenant:resource owner 45000",
            $"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 tenant:resource owner",
        });
    }

    [Test]
    public async Task LockScripts_FallBackToEvalAndSendLuaKeysAndArgs()
    {
        await using var server = new FakeRespServer(
            "-NOSCRIPT No matching script. Please use EVAL.\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Locks.ReleaseAsync("resource", "owner")).IsTrue();

        var commands = server.ReceivedCommands;
        await Assert.That(commands[0]).IsEqualTo($"EVALSHA {LockCommands.ReleaseScript.Sha1} 1 resource owner");
        await Assert.That(commands[1]).StartsWith("EVAL ");
        await Assert.That(commands[1]).Contains("redis.call('GET', KEYS[1]) == ARGV[1]");
        await Assert.That(commands[1]).Contains("redis.call('DEL', KEYS[1])");
        await Assert.That(commands[1]).EndsWith(" 1 resource owner");
    }

    [Test]
    public async Task LockCommands_ValidateTokenAndExpiryBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Locks.TakeAsync("resource", RespireValue.Null, TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.TakeAsync("resource", "", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.TakeAsync("resource", "owner", TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Locks.TakeAsync("resource", "owner", TimeSpan.FromTicks(1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Locks.ReleaseAsync("resource", RespireValue.Null))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.ExtendAsync("resource", "", TimeSpan.FromSeconds(1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Locks.ExtendAsync("resource", "owner", TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
