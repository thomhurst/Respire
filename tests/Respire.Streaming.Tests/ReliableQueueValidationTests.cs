using System.Reflection;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class ReliableQueueValidationTests
{
    [Test]
    [Arguments("")]
    [Arguments(":due")]
    [Arguments(":receipts")]
    [Arguments(":attempts")]
    [Arguments(":dead")]
    public async Task SendRejectsDedupAliasesBeforeSerializingOrExecutingCommands(string suffix)
    {
        var client = DispatchProxy.Create<IResolvingClient, AliasingClient>();
        var proxy = (AliasingClient)(object)client;
        proxy.Suffix = suffix;
        var serialized = false;
        var queue = new RespireReliableQueue<string>(client, "validation", value =>
        {
            serialized = true;
            return Encoding.UTF8.GetBytes(value);
        }, Encoding.UTF8.GetString);

        await Assert.That(async () => await queue.SendAsync("payload", "application-id"))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(serialized).IsFalse();
        await Assert.That(proxy.CommandCalls).IsEqualTo(0);
    }

    public interface IResolvingClient : IRespireClient, IRespireNameResolver { }

    public class AliasingClient : DispatchProxy
    {
        public string Suffix { get; set; } = "";
        public int CommandCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IRespireNameResolver.ResolveKey))
            {
                var key = (RespireKey)args![0]!;
                var name = Encoding.UTF8.GetString(key.ToBytes());
                var index = name.IndexOf(":dedup:", StringComparison.Ordinal);
                return index < 0 ? key : (RespireKey)(name[..index] + Suffix);
            }

            CommandCalls++;
            throw new InvalidOperationException("Validation must reject the alias before executing commands.");
        }
    }
}
