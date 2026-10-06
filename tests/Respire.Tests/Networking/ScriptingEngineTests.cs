using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ScriptingEngineTests
{
    private const string Missing = "ERR Could not find scripting engine 'lua'";
    private const string Absent = "# Scripting Engines\r\nengines_count:0\r\nengines_total_used_memory:0\r\n";
    private const string Present = "# Scripting Engines\r\nengines_count:1\r\nengine_0:name=LUA,module=lua,abi_version=4\r\n";
    private static byte[] Error(string message) => Encoding.UTF8.GetBytes($"-{message}\r\n");
    private static byte[] Bulk(string value) => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");

    private static async ValueTask<long> EvaluateAsync(RespireClient client, CancellationToken cancellationToken = default)
    {
        using var result = await client.ExecuteAsync("EVAL", ["return 42", 0], cancellationToken: cancellationToken);
        return result.AsInteger();
    }

    [Test]
    [Arguments(Absent, true)]
    [Arguments(Present, false)]
    [Arguments("", false)]
    [Arguments("engines_count:0\r\n", false)]
    [Arguments("# Scripting Engines\r\nengines_count:-1\r\n", false)]
    [Arguments("# Scripting Engines\r\nengines_count:1\r\n", false)]
    [Arguments("# Scripting Engines\r\nengines_count:0\r\nengines_count:0\r\n", false)]
    [Arguments("# Scripting Engines\r\nengines_count:1\r\nengine_1:name=OTHER\r\n", false)]
    [Arguments("# Scripting Engines\r\nengines_count:1\r\nengine_0:module=lua\r\n", false)]
    [Arguments("# Scripting Engines\r\nengines_count:1\r\nengine_0:name=OTHER,name=LUA\r\n", false)]
    [Arguments("# Scripting Engines\r\nengines_count:1\r\nengine_0:name=OTHER\r\n", true)]
    public async Task InventoryRequiresCompleteEvidence(string info, bool absent)
        => await Assert.That(ScriptingEngineInfo.ConfirmsAbsence(info, "lua")).IsEqualTo(absent);

    [Test]
    public async Task InventoryParsingSeparatesUnknownFromEmpty()
    {
        await Assert.That(ScriptingEngineInfo.ParseInventory("")).IsNull();
        await Assert.That(ScriptingEngineInfo.ParseInventory(Absent)!.Count).IsEqualTo(0);
        await Assert.That(ScriptingEngineInfo.ParseInventory(Present)!.Contains("lua")).IsTrue();
        await Assert.That(ScriptingEngineInfo.ParseInventory(
            "# Scripting Engines\nengines_count:2\nengine_0:name=lua\nengine_1:name=LUA\n")).IsNull();
    }

    [Test]
    [Arguments("EVAL", "return 42", "ERR Engine 'python' not found")]
    [Arguments("EVAL", "return 42", "ERR Could not find scripting engine 'python'")]
    [Arguments("EVAL_RO", "#!python\nreturn 42", "ERR Could not find scripting engine 'lua'")]
    [Arguments("EVALSHA", "digest", "ERR Could not find scripting engine 'lua'")]
    [Arguments("FCALL", "function", "ERR Engine 'python' not found")]
    public async Task ApplicationErrorsDoNotProbeAnotherEngine(string operation, string source, string message)
    {
        await using var server = new FakeRespServer(Error(message), Bulk(Absent));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var error = await Assert.That(async () =>
        {
            using var result = await client.ExecuteAsync(operation, [source, 0]);
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(message);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("#!python\nreturn 42", true)]
    [Arguments("#!'python'\nreturn 42", false)]
    public async Task CustomEngineRequiresUnambiguousSource(string source, bool classified)
    {
        await using var server = new FakeRespServer(Error("ERR Could not find scripting engine 'python'"), Bulk(Present));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        Exception? error = null;
        try { using var result = await client.ExecuteAsync("EVAL", [source, 0]); }
        catch (Exception caught) { error = caught; }
        await Assert.That(error is RespireScriptingEngineUnavailableException).IsEqualTo(classified);
        await Assert.That(error is RespireServerException).IsEqualTo(!classified);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(classified ? 2 : 1);
    }

    [Test]
    public async Task Resp3VerbatimInventoryConfirmsAbsence()
    {
        var info = Encoding.UTF8.GetBytes($"={Encoding.UTF8.GetByteCount(Absent) + 4}\r\ntxt:{Absent}\r\n");
        await using var server = new FakeRespServer(Error(Missing), info);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await EvaluateAsync(client))
            .ThrowsExactly<RespireScriptingEngineUnavailableException>();
    }

    [Test]
    public async Task DisconnectedProbePreservesOriginalCommandError()
    {
        await using var server = new FakeRespServer(Error(Missing)) { CloseConnectionAfterCommand = 2 };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var error = await Assert.That(async () => await EvaluateAsync(client))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(Missing);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimedOutProbePreservesOriginalCommandError(bool disableCommandTimeout)
    {
        await using var server = new FakeRespServer(Error(Missing))
        {
            SuppressReply = command => command == "INFO scriptingengines"
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            CommandTimeout = disableCommandTimeout ? null : TimeSpan.FromSeconds(10),
        });
        var error = await Assert.That(async () => await EvaluateAsync(client)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(Missing);
    }

    [Test]
    public async Task EvalFallbackConfirmsAbsenceOnSameConnection()
    {
        await using var server = new FakeRespServer(Error("NOSCRIPT No matching script."), Error(Missing), Bulk(Absent));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var error = await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return 42")))
            .ThrowsExactly<RespireScriptingEngineUnavailableException>();
        await Assert.That(error!.Engine).IsEqualTo("lua");
        await Assert.That(error.ServerError.Message).IsEqualTo(Missing);
        await Assert.That(error.ServerError.CommandName).IsEqualTo("EVAL");
        await Assert.That(error.Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("INFO scriptingengines");
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task PresentUnknownAndDeniedPreserveOriginalError(int response)
    {
        await using var server = new FakeRespServer(Error(Missing), response switch
        {
            0 => Bulk(Present), 1 => Bulk(""), _ => Error("NOPERM no permission for INFO")
        });
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var error = await Assert.That(async () => await EvaluateAsync(client))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(Missing);
    }

    [Test]
    [Arguments("ERR script error")]
    [Arguments("NOPERM no permission for EVALSHA")]
    [Arguments("ERR Function not found")]
    public async Task OtherErrorsDoNotProbe(string message)
    {
        await using var server = new FakeRespServer(Error(message));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var error = await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return 42")))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(message);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SuccessfulFallbackDoesNotProbe()
    {
        await using var server = new FakeRespServer(Error("NOSCRIPT No matching script."), ":42\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return 42"))).IsEqualTo(42);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ModuleChangesAreNotNegativelyCached()
    {
        await using var server = new FakeRespServer(Error(Missing), Bulk(Absent), ":42\r\n"u8.ToArray(), Error(Missing), Bulk(Present));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await EvaluateAsync(client)).ThrowsExactly<RespireScriptingEngineUnavailableException>();
        await Assert.That(await EvaluateAsync(client)).IsEqualTo(42);
        await Assert.That(async () => await EvaluateAsync(client)).ThrowsExactly<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(5);
    }

    [Test]
    public async Task ReplacementConnectionUsesFreshEvidence()
    {
        await using var server = new FakeRespServer(2, Error(Missing))
        {
            ReplyOverride = (id, command) => command == "INFO scriptingengines" ? Bulk(id == 0 ? Absent : Present) : null
        };
        await using (var first = await FakeRespServer.ConnectClientAsync(server.Port))
            await Assert.That(async () => await EvaluateAsync(first))
                .ThrowsExactly<RespireScriptingEngineUnavailableException>();
        await using var second = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await EvaluateAsync(second))
            .ThrowsExactly<RespireServerException>();
    }

    [Test]
    public async Task ConcurrentFailuresKeepResponseOrder()
    {
        await using var server = new FakeRespServer(Error(Missing))
        {
            ReplyOverride = (_, command) => command == "INFO scriptingengines" ? Bulk(Absent) : null
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            await Assert.That(async () => await EvaluateAsync(client))
                .ThrowsExactly<RespireScriptingEngineUnavailableException>()));
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(16);
    }

    [Test]
    public async Task CancellationDuringProbeRemainsCancellation()
    {
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(Error(Missing), Bulk(Absent))
        {
            SuppressReply = command =>
            {
                if (command != "INFO scriptingengines") return false;
                probing.TrySetResult();
                return true;
            }
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var call = EvaluateAsync(client, cancellation.Token).AsTask();
        await probing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await call).Throws<OperationCanceledException>();
        await server.SendRawAsync(Bulk(Absent));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedLoadsUseEngineDetection(bool function)
    {
        await using var server = new FakeRespServer(Error(function ? "ERR Engine 'lua' not found" : Missing), Bulk(Absent));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () =>
        {
            if (function) await client.Functions.LoadAsync("#!lua name=sample\nreturn 1");
            else await client.Scripts.LoadAsync(RespireScript.Create("return 42"));
        }).ThrowsExactly<RespireScriptingEngineUnavailableException>();
    }

    [Test]
    public async Task BatchClassifiesScriptError()
    {
        await using var server = new FakeRespServer(Error(Missing), Bulk(Absent));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var pending = batch.Scripts.Evaluate(RespireScript.Create("return 42"));
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        await Assert.That(pending.Error).IsTypeOf<RespireScriptingEngineUnavailableException>();
    }

    [Test]
    public async Task TransactionClassifiesWithoutReplay()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(),
            Encoding.UTF8.GetBytes($"*1\r\n-{Missing}\r\n"), Bulk(Absent));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Scripts.Evaluate(RespireScript.Create("return 42"));
        await transaction.CommitAsync();
        await Assert.That(pending.Error).IsTypeOf<RespireScriptingEngineUnavailableException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransactionSharesOnlyThisReplyInventory(bool denied)
    {
        var queued = "+QUEUED\r\n"u8.ToArray();
        var executed = Encoding.UTF8.GetBytes($"*2\r\n-{Missing}\r\n-{Missing}\r\n");
        await using var server = new FakeRespServer(FakeRespServer.OkReply, queued, queued, executed,
            denied ? Error("NOPERM no INFO") : Bulk(Absent));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        var first = transaction.Scripts.Evaluate(RespireScript.Create("return 42"));
        var second = transaction.Scripts.Evaluate(RespireScript.Create("return 43", readOnly: true));
        await transaction.CommitAsync();
        await Assert.That(first.Error is RespireScriptingEngineUnavailableException).IsEqualTo(!denied);
        await Assert.That(second.Error is RespireScriptingEngineUnavailableException).IsEqualTo(!denied);
        var firstError = first.Error is RespireScriptingEngineUnavailableException firstMissing ? firstMissing.ServerError : (RespireServerException)first.Error!;
        var secondError = second.Error is RespireScriptingEngineUnavailableException secondMissing ? secondMissing.ServerError : (RespireServerException)second.Error!;
        await Assert.That(firstError.CommandName).IsEqualTo("EVAL");
        await Assert.That(secondError.CommandName).IsEqualTo("EVAL_RO");
        await Assert.That(server.ReceivedCommands.Count(command => command == "INFO scriptingengines")).IsEqualTo(1);
    }
}
