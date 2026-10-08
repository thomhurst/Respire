using System.Buffers;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Internal;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Respire.SignalR.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.SignalR.Tests;

public class ProtocolTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvocationEnvelopeMatchesMicrosoftBytes(bool result)
    {
        await using var services = new ServiceCollection().AddLogging().AddSignalR().AddMessagePackProtocol()
            .Services.BuildServiceProvider();
        var resolver = services.GetRequiredService<IHubProtocolResolver>();
        var serializer = new DefaultHubMessageSerializer(resolver, null, null);
        var ours = new RedisProtocol(serializer);
        // Test-only reflection checks the published Microsoft implementation, not a second local codec.
        var assembly = typeof(RedisHubLifetimeManager<>).Assembly;
        var serializerType = assembly.GetType("Microsoft.AspNetCore.SignalR.Internal.DefaultHubMessageSerializer", true)!;
        var microsoftSerializer = Activator.CreateInstance(serializerType, resolver,
            resolver.AllProtocols.Select(protocol => protocol.Name).ToList(), null)!;
        var protocolType = assembly.GetType("Microsoft.AspNetCore.SignalR.StackExchangeRedis.Internal.RedisProtocol", true)!;
        var microsoft = Activator.CreateInstance(protocolType, microsoftSerializer)!;
        var args = new object?[] { "event", new object?[] { "é", 42 }, result ? "invocation" : null,
            new[] { "excluded" }, result ? "return" : null };
        var expected = (byte[])protocolType.GetMethod("WriteInvocation")!.Invoke(microsoft, args)!;
        var actual = ours.WriteInvocation("event", ["é", 42], result ? "invocation" : null,
            ["excluded"], result ? "return" : null);
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
        var decoded = RedisProtocol.ReadInvocation(expected);
        await Assert.That(decoded.InvocationId).IsEqualTo(result ? "invocation" : null);
        await Assert.That(decoded.ExcludedConnectionIds!.Single()).IsEqualTo("excluded");
    }

    [Test]
    public async Task ChannelNamesAndControlEnvelopesMatchMicrosoft()
    {
        var channels = new RedisChannels("Hub", "server");
        await Assert.That(channels.All).IsEqualTo("Hub:all");
        await Assert.That(channels.Connection("id")).IsEqualTo("Hub:connection:id");
        await Assert.That(channels.Group("group")).IsEqualTo("Hub:group:group");
        await Assert.That(channels.User("user")).IsEqualTo("Hub:user:user");
        await Assert.That(channels.GroupManagement).IsEqualTo("Hub:internal:groups");
        await Assert.That(channels.Ack("server")).IsEqualTo("Hub:internal:ack:server");
        await Assert.That(channels.ReturnResults).IsEqualTo("Hub:internal:return:server");
        await Assert.That(RedisProtocol.WriteAck(42).SequenceEqual(new byte[] { 0x91, 0x2a })).IsTrue();
        var group = RedisProtocol.ReadGroupCommand(RedisProtocol.WriteGroupCommand(new(42, "server", GroupAction.Add, "group", "connection")));
        await Assert.That(group.Id).IsEqualTo(42);
        await Assert.That(group.Action).IsEqualTo(GroupAction.Add);
        await Assert.That(group.ConnectionId).IsEqualTo("connection");
        var completion = RedisProtocol.ReadCompletion(RedisProtocol.WriteCompletionMessage(new byte[] { 1, 2, 3 }, "json"));
        await Assert.That(completion.CompletionMessage.ToArray().SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task MalformedEnvelopeCannotAllocateFromUntrustedCounts(int shape)
    {
        var bytes = shape switch
        {
            0 => new byte[] { 0x92, 0xdd, 0x7f, 0xff, 0xff, 0xff, 0x80 },
            1 => new byte[] { 0x92, 0x90, 0xdf, 0x7f, 0xff, 0xff, 0xff },
            _ => new byte[] { 0x91, 0x00 },
        };
        if (shape < 2)
            await Assert.That(() => RedisProtocol.ReadInvocation(bytes)).ThrowsExactly<EndOfStreamException>();
        else
            await Assert.That(() => RedisProtocol.ReadInvocation(bytes)).ThrowsExactly<InvalidDataException>();
    }
}
