using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ConditionalTransactionTests
{
    private static readonly byte[] Queued = "+QUEUED\r\n"u8.ToArray();

    private static byte[] Info(string version)
    {
        var info = $"server_name:valkey\r\nvalkey_version:{version}\r\n";
        return Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(info)}\r\n{info}\r\n");
    }

    [Test]
    [Arguments("IFEQ", true)]
    [Arguments("IFEQ", false)]
    [Arguments("IFNE", true)]
    [Arguments("IFNE", false)]
    [Arguments("NX", true)]
    [Arguments("NX", false)]
    [Arguments("XX", true)]
    [Arguments("XX", false)]
    public async Task CommitEncodesConditionAndCompletesOrAbortsPendings(string token, bool commit)
    {
        await using var server = new FakeRespServer(Info("9.2.0"), FakeRespServer.OkReply, Queued,
            commit ? "*1\r\n:1\r\n"u8.ToArray() : "*-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Increment("counter");
        var condition = token switch
        {
            "IFEQ" => RespireCondition.ValueEquals("key", "old"),
            "IFNE" => RespireCondition.ValueNotEquals("key", "old"),
            "NX" => RespireCondition.KeyNotExists("key"),
            _ => RespireCondition.KeyExists("key"),
        };

        await Assert.That(await transaction.CommitAsync(condition)).IsEqualTo(commit);
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo(
            $"EXEC {token} key" + (token is "IFEQ" or "IFNE" ? " old" : ""));
        await Assert.That(pending.Status).IsEqualTo(commit ? RespirePendingStatus.Succeeded : RespirePendingStatus.Aborted);
        if (commit) await Assert.That(pending.Result).IsEqualTo(1);
        else await Assert.That(() => _ = pending.Result).ThrowsExactly<RespireTransactionAbortedException>();
    }

    [Test]
    [Arguments("9.1.0")]
    [Arguments("8.1.0")]
    [Arguments("invalid")]
    public async Task UnsupportedServerFailsBeforeMultiAndFaultsPending(string version)
    {
        await using var server = new FakeRespServer(Info(version));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Increment("counter");
        await Assert.That(async () => await transaction.CommitAsync(RespireCondition.KeyExists("key")))
            .ThrowsExactly<NotSupportedException>();
        await Assert.That(pending.Error).IsTypeOf<NotSupportedException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["INFO SERVER"]);
    }

    [Test]
    public async Task DeniedCapabilityProbePreservesServerErrorAndSendsNoMulti()
    {
        await using var server = new FakeRespServer("-NOPERM INFO denied\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("key", "value");
        await Assert.That(async () => await transaction.CommitAsync(RespireCondition.KeyExists("key")))
            .ThrowsExactly<NotSupportedException>();
        await Assert.That(pending.Error!.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["INFO SERVER"]);
    }

    [Test]
    public async Task InvalidConditionsLeaveTransactionAvailableForValidCommit()
    {
        await using var server = new FakeRespServer(Info("9.2.0"), FakeRespServer.OkReply, Queued,
            "*1\r\n:1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Increment("counter");
        await Assert.That(() => RespireCondition.ValueEquals("key", default)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => RespireCondition.ValueNotEquals("key", default)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(async () => await transaction.CommitAsync((RespireCondition)null!)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(async () => await transaction.CommitAsync(Array.Empty<RespireCondition>()))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Pending);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(await transaction.CommitAsync(RespireCondition.KeyExists("key"))).IsTrue();
        await Assert.That(async () => await transaction.CommitAsync(RespireCondition.KeyExists("key")))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task EmptyConditionalCommitStillValidatesAndCachesSupport()
    {
        await using var server = new FakeRespServer(Info("9.2.0-rc1"), FakeRespServer.OkReply,
            "*-1\r\n"u8.ToArray(), FakeRespServer.OkReply, "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var first = client.CreateTransaction();
        await Assert.That(await first.CommitAsync(RespireCondition.KeyExists("key"))).IsFalse();
        await using var second = client.CreateTransaction();
        await Assert.That(await second.CommitAsync(RespireCondition.KeyNotExists("key"))).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command == "INFO SERVER")).IsEqualTo(1);
    }

    [Test]
    public async Task PipelineHookObservesConditionalAbort()
    {
        await using var server = new FakeRespServer(Info("9.2.0"), FakeRespServer.OkReply, Queued, "*-1\r\n"u8.ToArray());
        var hook = new AbortHook();
#pragma warning disable RESP010 // This transaction scenario owns its client and pipeline hook to observe only the scripted abort.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)], PipelineHook = hook,
        });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        _ = transaction.Set("key", "value");
        await Assert.That(await transaction.CommitAsync(RespireCondition.KeyNotExists("key"))).IsFalse();
        await Assert.That(hook.Outcome).IsEqualTo(RespirePipelineOutcome.Aborted);
    }

    private sealed class AbortHook : RespirePipelineHook
    {
        public RespirePipelineOutcome? Outcome { get; private set; }
        public override object? OnExecuting(RespirePipelineContext context) => null;
        public override void OnCompleted(RespirePipelineContext context, object? state, RespirePipelineOutcome outcome, Exception? error)
            => Outcome = outcome;
    }

    [Test]
    public async Task BinaryOperandsAreOwnedAndKeysUseClientPrefix()
    {
        await using var server = new FakeRespServer(Info("9.2.0"), FakeRespServer.OkReply, "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] key = [0, 255], value = [255, 0, 13, 10];
        var condition = RespireCondition.ValueEquals(key, value);
        key[0] = 42;
        value[0] = 42;
        await using var transaction = client.WithKeyPrefix("tenant:").CreateTransaction();
        await transaction.CommitAsync([condition, RespireCondition.KeyNotExists("other")]);
        var arguments = server.ReceivedArguments.Last();
        await Assert.That(arguments[2]).IsEquivalentTo("tenant:"u8.ToArray().Concat(new byte[] { 0, 255 }), CollectionOrdering.Matching);
        await Assert.That(arguments[3]).IsEquivalentTo(new byte[] { 255, 0, 13, 10 }, CollectionOrdering.Matching);
        await Assert.That(Encoding.UTF8.GetString(arguments[5])).IsEqualTo("tenant:other");
    }
}
