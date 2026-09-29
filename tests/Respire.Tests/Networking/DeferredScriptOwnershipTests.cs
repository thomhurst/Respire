using System.Reflection;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DeferredScriptOwnershipTests
{
    private static readonly FieldInfo ResultOwner = typeof(RespireResult)
        .GetField("_owner", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo ValueFlags = typeof(RespValue)
        .GetField("_flags", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo PendingValue = typeof(RespirePending<RespireResult>)
        .GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static byte[] NestedReply => "*2\r\n$5\r\nouter\r\n*2\r\n$5\r\ninner\r\n:7\r\n"u8.ToArray();

    [Test]
    public async Task UnreadBatchScriptResult_DoesNotOwnPooledBuffersWhenAnotherCommandFails()
    {
        await using var server = new FakeRespServer(NestedReply, "-WRONGTYPE unrelated command failed\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var pending = batch.Scripts.Evaluate(RespireScript.Create("return {'outer', {'inner', 7}}"));
        _ = batch.Strings.GetString("wrong-type");
        var execution = await batch.ExecuteAsync();
        batch.Dispose();

        await Assert.That(execution.FailureCount).IsEqualTo(1);
        await Assert.That(pending.HasResult).IsTrue();
        // Inspect the pending's retained storage without consuming its public Result.
        var unread = (RespireResult)PendingValue.GetValue(pending)!;
        await Assert.That(OwnsPooledStorage(unread)).IsFalse();
        await Assert.That(unread[1][0].AsString()).IsEqualTo("inner");
        await Assert.That(unread[1][1].AsInteger()).IsEqualTo(7);
        // No result disposal: this is the supported deferred-result usage being protected.
    }

    [Test]
    public async Task TransactionScriptResult_DoesNotOwnPooledBuffersAfterTransactionDisposal()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), [.. "*1\r\n"u8, .. NestedReply]);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var transaction = client.CreateTransaction();
        var pending = transaction.Scripts.Evaluate(RespireScript.Create("return {'outer', {'inner', 7}}"));
        await transaction.CommitAsync();
        await transaction.DisposeAsync();

        var result = pending.Result;
        var nested = result[1][0];
        await Assert.That(OwnsPooledStorage(result)).IsFalse();
        await Assert.That(nested.AsString()).IsEqualTo("inner");
        result.Dispose();
        result.Dispose();
        await Assert.That(result.IsDisposed).IsTrue();
        await Assert.That(nested.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ImmediateScriptResult_StillOwnsPooledBuffers()
    {
        await using var server = new FakeRespServer(NestedReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return {'outer', {'inner', 7}}"));

        // Positive control: the ownership check must detect both aggregates and payloads on the wire path.
        await Assert.That(OwnsPooledStorage(result)).IsTrue();
        await Assert.That(result[1][0].AsString()).IsEqualTo("inner");
    }

    private static bool OwnsPooledStorage(RespireResult result)
        => OwnsPooledStorage(((PooledValueOwner)ResultOwner.GetValue(result)!).Value);

    private static bool OwnsPooledStorage(RespValue value)
    {
        if ((RespValue.ValueFlags)ValueFlags.GetValue(value)! != RespValue.ValueFlags.None)
        {
            return true;
        }

        foreach (var child in value.AsArray())
        {
            if (OwnsPooledStorage(child))
            {
                return true;
            }
        }

        return false;
    }
}
