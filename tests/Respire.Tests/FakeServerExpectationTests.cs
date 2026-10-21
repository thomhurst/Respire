using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeServerExpectationTests
{
    [Test]
    public async Task Resp2SubscribedPingCanOverrideItsAllowedReply()
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions() with { Protocol = RespireProtocol.Resp2 });
        server.Expect("SUBSCRIBE", ["events"]);
        server.Expect("PING", [], response: RespireFakeResponse.Array(
            RespireFakeResponse.Bulk("pong"), RespireFakeResponse.Bulk("configured")));
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        using (var subscribed = await session.CommandAsync("SUBSCRIBE", "events")) { }
        using var ping = await session.CommandAsync("PING");
        await Assert.That(ping.AsArray()[0].AsString()).IsEqualTo("pong");
        await Assert.That(ping.AsArray()[1].AsString()).IsEqualTo("configured");
        server.VerifyAllExpectations();
    }

    [Test]
    public async Task DifferentFacetsAndClientTransactionsShareWireExpectations()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
#pragma warning restore RESP010
        server.Expect("HSET", ["hash", "field", "value"]);
        server.Expect("HGET", ["hash", "field"], response: RespireFakeResponse.Bulk("mocked"));
        server.Expect("RPUSH", ["list", "one", "two"]);
        server.Expect("MULTI", []);
        server.Expect("SET", ["key", "value"]);
        server.Expect("EXEC", []);
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        await client.Hashes.SetAsync("hash", "field", "value");
        await Assert.That(await client.Hashes.GetStringAsync("hash", "field")).IsEqualTo("mocked");
        await Assert.That(await client.Lists.RightPushAsync("list", "one", "two")).IsEqualTo(2);
        await using var transaction = client.CreateTransaction();
        var set = transaction.Set("key", "value");
        await transaction.CommitAsync();
        await set;
        server.VerifyAllExpectations();
    }

    [Test]
    public async Task StrictTransactionMismatchInvalidatesQueueWithoutExecutingWrites()
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        server.Expect("MULTI", []);
        server.Expect("SET", ["key", "value"]);
        server.Expect("EXEC", []);
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "key", "value"))
            await Assert.That(queued.AsString()).IsEqualTo("QUEUED");
        using (var rejected = await session.CommandAsync("GET", "wrong"))
            await Assert.That(rejected.GetErrorMessage()).Contains("Unexpected command");
        using (var aborted = await session.CommandAsync("EXEC"))
            await Assert.That(aborted.GetErrorMessage()).StartsWith("EXECABORT");
        var failure = await Assert.That(() => server.VerifyAllExpectations()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(failure!.Message).Contains("GET \"wrong\"");
        await Assert.That(failure.Message).Contains("strict rejection");
        server.ResetExpectations();
        using var absent = await session.CommandAsync("GET", "key");
        await Assert.That(absent.IsNull).IsTrue();
    }

    [Test]
    public async Task ResetDoesNotChangeAlreadyQueuedResponseOverrides()
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        server.Expect("GET", ["key"], response: RespireFakeResponse.Bulk("queued override"));
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("GET", "key")) { }
        server.VerifyAllExpectations();
        server.ResetExpectations();
        using var executed = await session.CommandAsync("EXEC");
        await Assert.That(executed.AsArray()[0].AsString()).IsEqualTo("queued override");
        await Assert.That(server.CommandHistory.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExpectationsAreOptInAndResetClearsRulesAndHistory()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        await Assert.That(server.ExpectationMode).IsEqualTo(RespireFakeExpectationMode.Disabled);
        await client.SetAsync("key", "original");
        await Assert.That(server.CommandHistory.Count).IsEqualTo(0);
        server.VerifyAllExpectations();

        server.Expect("GET", ["key"], response: RespireFakeResponse.Bulk("override"));
        await Assert.That(server.ExpectationMode).IsEqualTo(RespireFakeExpectationMode.Loose);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("override");
        var snapshot = server.CommandHistory;
        server.VerifyAllExpectations();
        server.ResetExpectations();
        await Assert.That(server.ExpectationMode).IsEqualTo(RespireFakeExpectationMode.Disabled);
        await Assert.That(server.CommandHistory.Count).IsEqualTo(0);
        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("original");
        server.VerifyAllExpectations();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExactBinaryArgumentsAreOwnedAndCommandNamesIgnoreCase(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        byte[] key = [0, 255, 128, 13, 10];
        byte[] value = [255, 0, 13, 10];
        server.Expect("sEt", [RespireFakeArgument.Exact(key), RespireFakeArgument.Exact(value)]);
        key[0] = 1;
        value[0] = 1;
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        using var set = await client.ExecuteAsync("SET", new byte[] { 0, 255, 128, 13, 10 }, new byte[] { 255, 0, 13, 10 });
        await Assert.That(set.AsString()).IsEqualTo("OK");
        server.VerifyAllExpectations();
        var recorded = server.CommandHistory.Single();
        await Assert.That(recorded.Matched).IsTrue();
        await Assert.That(recorded.Arguments.Count).IsEqualTo(2);
        await Assert.That(recorded.Arguments[0].ToArray()).IsEquivalentTo(new byte[] { 0, 255, 128, 13, 10 });
        await Assert.That(recorded.Arguments[1].ToArray()).IsEquivalentTo(new byte[] { 255, 0, 13, 10 });
    }

    [Test]
    public async Task DistinctInvalidUtf8ArgumentsDoNotMatchThroughReplacementCharacters()
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        server.Expect("GET", [RespireFakeArgument.Exact(new byte[] { 255 })]);
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        using (var wrong = await session.CommandBytesAsync("GET"u8.ToArray(), [254]))
            await Assert.That(wrong.IsError).IsTrue();
        using (var correct = await session.CommandBytesAsync("GET"u8.ToArray(), [255]))
            await Assert.That(correct.IsNull).IsTrue();
        await Assert.That(server.CommandHistory[0].Matched).IsFalse();
        await Assert.That(server.CommandHistory[1].Matched).IsTrue();
    }

    [Test]
    public async Task WildcardMatchesOneArgumentAndRequiresTheCompleteArgumentList()
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        server.Expect("SET", ["key", RespireFakeArgument.Any]);
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        using (var tooFew = await session.CommandAsync("SET", "key"))
            await Assert.That(tooFew.IsError).IsTrue();
        using (var tooMany = await session.CommandAsync("SET", "key", "value", "NX"))
            await Assert.That(tooMany.IsError).IsTrue();
        using (var wrongKey = await session.CommandAsync("SET", "other", "value"))
            await Assert.That(wrongKey.IsError).IsTrue();
        using (var matched = await session.CommandBytesAsync("SET"u8.ToArray(), "key"u8.ToArray(), [0, 255]))
            await Assert.That(matched.AsString()).IsEqualTo("OK");
        await Assert.That(server.CommandHistory.Select(command => command.Matched).ToArray())
            .IsEquivalentTo(new[] { false, false, false, true });
        await Assert.That(() => server.VerifyAllExpectations()).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task LooseModeAllowsOtherCommandsAndVerificationExplainsMissingCalls()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        server.Expect("GET", ["expected"], occurrences: 2);
        await client.SetAsync("other", "stored");
        await Assert.That(await client.GetStringAsync("other")).IsEqualTo("stored");
        await Assert.That(await client.GetStringAsync("expected")).IsNull();
        var error = await Assert.That(() => server.VerifyAllExpectations()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("GET");
        await Assert.That(error.Message).Contains("2");
        await Assert.That(error.Message).Contains("1");
        await Assert.That(error.Message).Contains("SET");
        await client.GetStringAsync("expected");
        server.VerifyAllExpectations();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExcessCallsFailVerificationAndStrictModeRejectsTheirMutation(bool strict)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        server.Expect("INCR", ["counter"]);
        server.ExpectationMode = strict ? RespireFakeExpectationMode.Strict : RespireFakeExpectationMode.Loose;
        using (var first = await client.ExecuteAsync("INCR", "counter"))
            await Assert.That(first.AsInteger()).IsEqualTo(1);
        if (strict)
            await Assert.That(async () => { using var reply = await client.ExecuteAsync("INCR", "counter"); })
                .Throws<RespireServerException>();
        else
        {
            using var second = await client.ExecuteAsync("INCR", "counter");
            await Assert.That(second.AsInteger()).IsEqualTo(2);
        }
        await Assert.That(() => server.VerifyAllExpectations()).ThrowsExactly<InvalidOperationException>();
        server.ResetExpectations();
        await Assert.That(await client.GetStringAsync("counter")).IsEqualTo(strict ? "1" : "2");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ResponseOverridesPreserveTypesAndOwnBinaryValues(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        byte[] value = [0, 255, 128, 13, 10];
        server.Expect("CUSTOM", [], response: RespireFakeResponse.Array(
            RespireFakeResponse.Simple("OK"), RespireFakeResponse.Bulk(value), RespireFakeResponse.Integer(-42),
            RespireFakeResponse.Null, RespireFakeResponse.Array(RespireFakeResponse.Bulk("nested")),
            RespireFakeResponse.Error("ERR nested failure")));
        value[0] = 99;
        using var reply = await client.ExecuteAsync("CUSTOM");
        await Assert.That(reply.Count).IsEqualTo(6);
        await Assert.That(reply[0].AsString()).IsEqualTo("OK");
        await Assert.That(reply[1].AsBytes()).IsEquivalentTo(new byte[] { 0, 255, 128, 13, 10 });
        await Assert.That(reply[2].AsInteger()).IsEqualTo(-42);
        await Assert.That(reply[3].IsNull).IsTrue();
        await Assert.That(reply[4][0].AsString()).IsEqualTo("nested");
        await Assert.That(reply[5].ErrorMessage).IsEqualTo("ERR nested failure");
        server.VerifyAllExpectations();
    }

    [Test]
    public async Task RulesConsumeInRegistrationOrderAndOverridesDoNotMutateState()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        await client.SetAsync("key", "original");
        server.Expect("GET", [RespireFakeArgument.Any], occurrences: 2, response: RespireFakeResponse.Bulk("first"));
        server.Expect("GET", ["key"], response: RespireFakeResponse.Bulk("second"));
        server.Expect("SET", ["key", "new"], response: RespireFakeResponse.Simple("OK"));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("first");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("first");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("second");
        await Assert.That(await client.SetAsync("key", "new")).IsTrue();
        server.VerifyAllExpectations();
        server.ResetExpectations();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("original");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PrefixedFacetsAndBatchErrorsKeepReplyOrder(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1, Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        server.Expect("SET", ["tenant:rejected", "bad"], response: RespireFakeResponse.Error("ERR expected failure"));
        server.Expect("SET", ["tenant:accepted", "ok"]);
        server.Expect("GET", ["tenant:accepted"]);
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        using var batch = client.WithKeyPrefix("tenant:").CreateBatch();
        var rejected = batch.Strings.Set("rejected", "bad");
        var accepted = batch.Strings.Set("accepted", "ok");
        var read = batch.Strings.GetString("accepted");
        await Assert.That(async () => await batch.ExecuteAsync()).Throws<RespireServerException>();
        var error = await Assert.That(async () => await rejected).Throws<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo("ERR expected failure");
        await Assert.That(await accepted).IsTrue();
        await Assert.That(await read).IsEqualTo("ok");
        server.VerifyAllExpectations();
        server.ResetExpectations();
        await Assert.That(await client.GetStringAsync("tenant:rejected")).IsNull();
    }

    [Test]
    public async Task ConcurrentConnectionsConsumeEachOccurrenceOnceAndHistoryIsASnapshot()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var first = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 2 });
        await using var second = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 2 });
#pragma warning restore RESP010
        server.Expect("INCR", ["counter"], occurrences: 32, response: RespireFakeResponse.Integer(10));
        server.Expect("INCR", ["counter"], occurrences: 32, response: RespireFakeResponse.Integer(20));
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        var before = server.CommandHistory;
        var calls = Enumerable.Range(0, 64).Select(async index =>
        {
            using var reply = await (index % 2 == 0 ? first : second).ExecuteAsync("INCR", "counter");
            return reply.AsInteger();
        });
        var replies = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(replies.Count(value => value == 10)).IsEqualTo(32);
        await Assert.That(replies.Count(value => value == 20)).IsEqualTo(32);
        await Assert.That(before.Count).IsEqualTo(0);
        var history = server.CommandHistory;
        await Assert.That(history.Count).IsEqualTo(64);
        await Assert.That(history.All(command => command.Matched)).IsTrue();
        await Assert.That(history.Select(command => command.ConnectionId).Distinct().Count() >= 2).IsTrue();
        await Assert.That(history.Select(command => command.Sequence).Distinct().Count()).IsEqualTo(64);
        await Assert.That(history.Zip(history.Skip(1), (left, right) => left.Sequence < right.Sequence).All(value => value)).IsTrue();
        server.VerifyAllExpectations();
    }

    [Test]
    public async Task FaultRepliesWinOverOverridesButStillCountReceivedExpectations()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        server.Expect("SET", ["key", "value"], response: RespireFakeResponse.Simple("OK"));
        using var fault = server.InjectFault("SET", RespireFakeFault.ReadOnly());
        var error = await Assert.That(async () => await client.SetAsync("key", "value")).Throws<RespireServerException>();
        await Assert.That(error!.Message).StartsWith("READONLY");
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(fault.ExecutionCount).IsEqualTo(0);
        server.VerifyAllExpectations();
        server.ResetExpectations();
        await Assert.That(await client.GetStringAsync("key")).IsNull();
    }

    [Test]
    public async Task StrictRejectionDoesNotConsumeAnInjectedFault()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        server.Expect("SET", ["expected", "value"]);
        server.ExpectationMode = RespireFakeExpectationMode.Strict;
        using var fault = server.InjectFault("SET", RespireFakeFault.Loading());
        var unexpected = await Assert.That(async () => await client.SetAsync("other", "value")).Throws<RespireServerException>();
        await Assert.That(unexpected!.Message).StartsWith("ERR");
        await Assert.That(fault.MatchedCount).IsEqualTo(0);
        var expected = await Assert.That(async () => await client.SetAsync("expected", "value")).Throws<RespireServerException>();
        await Assert.That(expected!.Message).StartsWith("LOADING");
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(server.CommandHistory.Last().Matched).IsTrue();
        await Assert.That(() => server.VerifyAllExpectations()).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TransactionCountsAtQueueTimeAndExecUsesSavedOverrides(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        server.Expect("SET", ["key", "value"], response: RespireFakeResponse.Error("ERR deferred failure"));
        server.Expect("GET", ["key"], response: RespireFakeResponse.Bulk("override"));
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var set = await session.CommandAsync("SET", "key", "value"))
            await Assert.That(set.AsString()).IsEqualTo("QUEUED");
        using (var get = await session.CommandAsync("GET", "key"))
            await Assert.That(get.AsString()).IsEqualTo("QUEUED");
        server.VerifyAllExpectations();
        using var executed = await session.CommandAsync("EXEC");
        await Assert.That(executed.AsArray()[0].GetErrorMessage()).IsEqualTo("ERR deferred failure");
        await Assert.That(executed.AsArray()[1].AsString()).IsEqualTo("override");
        await Assert.That(server.CommandHistory.Count(command => command.Command == "SET")).IsEqualTo(1);
        await Assert.That(server.CommandHistory.Count(command => command.Command == "GET")).IsEqualTo(1);
        server.VerifyAllExpectations();
        server.ResetExpectations();
        using var absent = await session.CommandAsync("GET", "key");
        await Assert.That(absent.IsNull).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiscardAndWatchAbortKeepReceivedExpectationsSatisfied(bool watchAbort)
    {
        await using var server = new RespireFakeServer();
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var writer = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        server.Expect("SET", ["queued", "value"], response: RespireFakeResponse.Simple("OK"));
        if (watchAbort)
        {
            using var watch = await session.CommandAsync("WATCH", "watched");
            await writer.SetAsync("watched", "changed");
        }
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("SET", "queued", "value"))
            await Assert.That(queued.AsString()).IsEqualTo("QUEUED");
        server.VerifyAllExpectations();
        using (var done = await session.CommandAsync(watchAbort ? "EXEC" : "DISCARD"))
        {
            if (watchAbort) await Assert.That(done.IsNull).IsTrue();
            else await Assert.That(done.AsString()).IsEqualTo("OK");
        }
        server.VerifyAllExpectations();
        await Assert.That(await writer.GetStringAsync("queued")).IsNull();
    }

    [Test]
    [Arguments("HELLO")]
    [Arguments("CLIENT")]
    [Arguments("MULTI")]
    [Arguments("EXEC")]
    [Arguments("DISCARD")]
    [Arguments("WATCH")]
    [Arguments("UNWATCH")]
    [Arguments("SUBSCRIBE")]
    [Arguments("UNSUBSCRIBE")]
    public async Task ControlCommandsRejectResponseOverrides(string command)
    {
        await using var server = new RespireFakeServer();
        await Assert.That(() => server.Expect(command, [], response: RespireFakeResponse.Simple("OK")))
            .Throws<ArgumentException>();
    }
}
