using System.Reflection;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class ReliableTopicValidationTests
{
    [Test]
    public async Task InvalidArgumentsAndCancellationFailBeforeCommandsOrSerialization()
    {
        var client = DispatchProxy.Create<IResolvingClient, ResolvingClient>();
        var serialized = false;
        byte[] Serialize(string value) { serialized = true; return Encoding.UTF8.GetBytes(value); }
        foreach (var name in new[] { "", " ", "topic{tag}", "topic}", "\ud800" })
            await Assert.That(() => new RespireReliableTopic<string>(client, name, Serialize, Encoding.UTF8.GetString))
                .Throws<ArgumentException>();
        await Assert.That(() => new RespireReliableTopic<string>(client, "topic", Serialize, Encoding.UTF8.GetString, deliveryLimit: 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireReliableTopic<string>(client, "topic", Serialize, Encoding.UTF8.GetString, maximumLength: 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireReliableTopic<string>(client, "topic", Serialize, Encoding.UTF8.GetString, maximumAge: TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireReliableTopic<string>(client, "topic", Serialize, Encoding.UTF8.GetString,
            maximumLength: 1, maximumAge: TimeSpan.FromMinutes(1))).Throws<ArgumentException>();
        var topic = new RespireReliableTopic<string>(client, "topic", Serialize, Encoding.UTF8.GetString);
        await Assert.That(async () => await topic.PublishAsync("payload", new(true))).Throws<OperationCanceledException>();
        await Assert.That(async () => await topic.SubscribeAsync("cancelled", cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        var nullCodec = new RespireReliableTopic<string>(client, "topic", _ => null!, Encoding.UTF8.GetString);
        await Assert.That(async () => await nullCodec.PublishAsync("payload")).Throws<InvalidOperationException>();
        foreach (var name in new[] { "", " ", "\ud800" })
            await Assert.That(async () => await topic.SubscribeAsync(name)).Throws<ArgumentException>();
        await Assert.That(serialized).IsFalse();
        await Assert.That(((ResolvingClient)(object)client).CommandCalls).IsEqualTo(0);
    }

    [Test]
    public async Task AliasedSubscriptionKeysAreRejectedBeforeGroupCreation()
    {
        var client = DispatchProxy.Create<IResolvingClient, ResolvingClient>();
        var proxy = (ResolvingClient)(object)client;
        proxy.Alias = true;
        var topic = new RespireReliableTopic<string>(client, "topic", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString);
        await Assert.That(async () => await topic.SubscribeAsync("name")).Throws<ArgumentException>();
        await Assert.That(proxy.CommandCalls).IsEqualTo(0);
    }

    public interface IResolvingClient : IRespireClient, IRespireNameResolver { }

    public class ResolvingClient : DispatchProxy
    {
        public bool Alias { get; set; }
        public int CommandCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IRespireNameResolver.ResolveKey))
                return Alias ? (RespireKey)"{topic}:alias" : args![0];
            CommandCalls++;
            throw new InvalidOperationException("Validation must fail before executing commands.");
        }
    }
}
