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
        var execution = await batch.TryExecuteAsync();
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
    public async Task ImmediateScriptResult_DoesNotOwnPooledBuffers()
    {
        await using var server = new FakeRespServer(NestedReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return {'outer', {'inner', 7}}"));

        await Assert.That(OwnsPooledStorage(result)).IsFalse();
        await Assert.That(result[1][0].AsString()).IsEqualTo("inner");
    }

    [Test]
    [Arguments("catalog")]
    [Arguments("raw")]
    [Arguments("flags")]
    [Arguments("interpolated")]
    [Arguments("script")]
    [Arguments("script-span")]
    [Arguments("function")]
    [Arguments("function-span")]
    [Arguments("script-single")]
    [Arguments("script-span-single")]
    [Arguments("function-single")]
    [Arguments("function-span-single")]
    public async Task ImmediateResults_AreOwnedUnlessExplicitlyLeased(string api)
    {
        await using var server = new FakeRespServer(NestedReply);
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = owner;
        var script = RespireScript.Create("return {'outer', {'inner', 7}}");
        var function = RespireFunction.Create("nested");
        using var cancellation = new CancellationTokenSource();
        // Deliberately never dispose the ordinary result, including its nested views.
        var result = await (api switch
        {
            "catalog" => client.ExecuteAsync(RespireCommands.String.MGET, "key"),
            "raw" => client.ExecuteAsync("MGET", "key"),
            "flags" => client.ExecuteAsync("MGET", ["key"], cancellationToken: cancellation.Token),
            "interpolated" => client.ExecuteAsync($"MGET {"key"}"),
            "script" => client.Scripts.ExecuteAsync(script),
            "script-span" => client.Scripts.ExecuteSpanAsync(script, [], []),
            "function" => client.Functions.ExecuteAsync(function),
            "script-single" => client.Scripts.ExecuteAsync(script, "key", cancellationToken: cancellation.Token),
            "script-span-single" => client.Scripts.ExecuteSpanAsync(script, "key", [], cancellation.Token),
            "function-single" => client.Functions.ExecuteAsync(function, "key", cancellationToken: cancellation.Token),
            "function-span-single" => client.Functions.ExecuteSpanAsync(function, "key", [], cancellation.Token),
            _ => client.Functions.ExecuteSpanAsync(function, [], []),
        });
        var nested = result[1][0];
        await Assert.That(OwnsPooledStorage(result)).IsFalse();

        var lease = await (api switch
        {
            "catalog" => client.ExecuteLeaseAsync(RespireCommands.String.MGET, "key"),
            "raw" => client.ExecuteLeaseAsync("MGET", "key"),
            "flags" => client.ExecuteLeaseAsync("MGET", ["key"], cancellationToken: cancellation.Token),
            "interpolated" => client.ExecuteLeaseAsync($"MGET {"key"}"),
            "script" => client.Scripts.ExecuteLeaseAsync(script),
            "script-span" => client.Scripts.ExecuteLeaseSpanAsync(script, [], []),
            "function" => client.Functions.ExecuteLeaseAsync(function),
            "script-single" => client.Scripts.ExecuteLeaseAsync(script, "key", cancellationToken: cancellation.Token),
            "script-span-single" => client.Scripts.ExecuteLeaseSpanAsync(script, "key", [], cancellation.Token),
            "function-single" => client.Functions.ExecuteLeaseAsync(function, "key", cancellationToken: cancellation.Token),
            "function-span-single" => client.Functions.ExecuteLeaseSpanAsync(function, "key", [], cancellation.Token),
            _ => client.Functions.ExecuteLeaseSpanAsync(function, [], []),
        });
        var copy = lease;
        var leasedChild = lease[1][0];
        // Positive control ensures the check detects actual wire-buffer ownership.
        await Assert.That(OwnsPooledStorage(lease)).IsTrue();
        lease.Dispose();
        copy.Dispose();
        await Assert.That(leasedChild.IsDisposed).IsTrue();
        await Assert.That(() => leasedChild.AsString()).ThrowsExactly<ObjectDisposedException>();
        await owner.DisposeAsync();
        await Assert.That(nested.AsString()).IsEqualTo("inner");
        await Assert.That(result[1][1].AsInteger()).IsEqualTo(7);
    }

    [Test]
    public async Task AsyncBatchDisposal_DiscardsUnsentCommandsAndPreservesCompletedResults()
    {
        await using var server = new FakeRespServer(NestedReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespirePending<RespireResult> discarded;
        await using (var batch = client.CreateBatch())
        {
            discarded = batch.Execute("MGET", "key");
        }
        await Assert.That(() => discarded.Result).ThrowsExactly<RespireBatchDiscardedException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);

        RespirePending<RespireResult> completed;
        await using (var batch = client.CreateBatch())
        {
            completed = batch.Execute("MGET", "key");
            await batch.ExecuteAsync();
            await batch.DisposeAsync();
        }
        await Assert.That(completed.Result[1][0].AsString()).IsEqualTo("inner");
        await Assert.That(OwnsPooledStorage(completed.Result)).IsFalse();
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
