using System.Collections.Concurrent;
using System.Reflection;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchCommandHookTests
{
    [Test]
    [Arguments("search", "FT.SEARCH")]
    [Arguments("aggregate", "FT.AGGREGATE")]
    [Arguments("cursor", "FT.AGGREGATE")]
    [Arguments("read", "FT.CURSOR READ")]
    [Arguments("delete", "FT.CURSOR DEL")]
    [Arguments("hybrid", "FT.HYBRID")]
    [Arguments("profile", "FT.PROFILE")]
    [Arguments("config", "FT.CONFIG GET")]
    [Arguments("config-set", "FT.CONFIG SET")]
    [Arguments("info", "FT.INFO")]
    [Arguments("aliases", "FT.ALIASLIST")]
    [Arguments("suggestion", "FT.SUGADD")]
    [Arguments("synonyms", "FT.SYNDUMP")]
    [Arguments("spell", "FT.SPELLCHECK")]
    [Arguments("dictionary", "FT.DICTADD")]
    public async Task RejectionPrecedesWireAndCompletesOnce(string route, string operation)
    {
        await using var server = Server();
        var rejected = new InvalidOperationException("rejected");
        var hook = new Hook { Before = _ => throw rejected };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;

        var error = await Failure(() => Call(client.Search, route));

        await Assert.That(error).IsSameReferenceAs(rejected);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Command).IsEqualTo(operation);
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await Assert.That(completion.State).IsNull();
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("search")]
    [Arguments("aggregate")]
    [Arguments("read")]
    [Arguments("hybrid")]
    [Arguments("profile")]
    [Arguments("config")]
    [Arguments("config-set")]
    [Arguments("info")]
    [Arguments("aliases")]
    [Arguments("suggestion")]
    [Arguments("synonyms")]
    [Arguments("spell")]
    [Arguments("dictionary")]
    public async Task MalformedTypedReplyCompletesWithTheConversionError(string route)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("FT.", StringComparison.Ordinal)
            ? "$3\r\nbad\r\n"u8.ToArray() : null;
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));

        var error = await Failure(() => Call(client.Search, route));

        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await Assert.That(completion.State).IsSameReferenceAs(hook.Entered.Single());
    }

    [Test]
    public async Task PreCanceledSearchIsAdmittedAndCompletedWithoutWireWork()
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var baseline = server.CommandsSeen;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Failure(() => Call(client.Search, "search", cancellation.Token));

        await Assert.That(error).IsTypeOf<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
    }

    [Test]
    public async Task CancellationAfterDispatchCompletesOnceAndLeavesFollowingCommandUsable()
    {
        await using var server = Server();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.GateCommand("FT.SEARCH", release.Task, () => received.TrySetResult());
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var pending = Call(client.Search, "search", cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var error = await Failure(() => pending);
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        }
        finally { release.TrySetResult(); }

        await Assert.That(await client.Strings.GetStringAsync("after")).IsEqualTo("x");
        await Assert.That(hook.Completed.Count(c => c.Command == "FT.SEARCH")).IsEqualTo(1);
    }

    [Test]
    public async Task RejectedMutationPreservesCacheAndAcceptedMutationInvalidatesIt()
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET cached" => "$1\r\nx\r\n"u8.ToArray(),
            "FT._LIST" => "*0\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(),
        });
        await client.Strings.GetStringAsync("cached");
        hook.Before = context => { if (context.Command == "FT.ALIASADD") throw new InvalidOperationException("rejected"); };
        await Failure(() => client.Search.AddAliasAsync("alias", "idx").AsTask());
        await client.Search.ListIndexesAsync();
        await client.Strings.GetStringAsync("cached");
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET cached")).IsEqualTo(1);

        hook.Before = null;
        await client.Search.AddAliasAsync("alias", "idx");
        await client.Strings.GetStringAsync("cached");
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET cached")).IsEqualTo(2);
        await Assert.That(hook.Completed.Count(c => c.Command == "FT.ALIASADD")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HybridProbeBelongsToFinalTranslatedFailure(bool probeDenied)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("FT.HYBRID", StringComparison.Ordinal) => "-ERR unknown command 'FT.HYBRID'\r\n"u8.ToArray(),
            "COMMAND INFO FT.HYBRID" => probeDenied ? "-NOPERM denied\r\n"u8.ToArray() : "*1\r\n$-1\r\n"u8.ToArray(),
            _ => null,
        };
        var probeFinished = false;
        var hook = new Hook
        {
            Before = context => { if (context.Command == "COMMAND INFO") throw new InvalidOperationException("application probe rejected"); },
            After = _ => probeFinished = server.ReceivedCommands.Contains("COMMAND INFO FT.HYBRID"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));

        var error = await Failure(() => Call(client.Search, "hybrid"));

        await Assert.That(error).IsTypeOf<NotSupportedException>();
        await Assert.That(probeFinished).IsTrue();
        await Assert.That(hook.Completed.Single().Command).IsEqualTo("FT.HYBRID");
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
    }

    [Test]
    [Arguments("cursor", false)]
    [Arguments("cursor", true)]
    [Arguments("read", false)]
    [Arguments("read", true)]
    public async Task MalformedPageCleansUpBeforeCompletionWithoutSeparateAdmission(string route, bool cleanupFails)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) || command.StartsWith("FT.CURSOR READ", StringComparison.Ordinal)
                => "*2\r\n+bad\r\n:42\r\n"u8.ToArray(),
            "FT.CURSOR DEL idx 42" => cleanupFails ? "-ERR expired\r\n"u8.ToArray() : "+OK\r\n"u8.ToArray(),
            _ => null,
        };
        var cleanedUp = false;
        var hook = new Hook
        {
            Before = context => { if (context.Command == "FT.CURSOR DEL") throw new InvalidOperationException("application delete rejected"); },
            After = _ => cleanedUp = server.ReceivedCommands.Contains("FT.CURSOR DEL idx 42"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));

        var error = await Failure(() => Call(client.Search, route));

        await Assert.That(cleanedUp).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EnumerationOwnsPagesAndEarlyExitCleanupHasNoHook(bool breakEarly)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) => "*2\r\n*1\r\n:0\r\n:42\r\n"u8.ToArray(),
            "FT.CURSOR READ idx 42" => "*2\r\n*1\r\n:0\r\n:0\r\n"u8.ToArray(),
            "FT.CURSOR DEL idx 42" => "+OK\r\n"u8.ToArray(),
            _ => null,
        };
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var pages = 0;

        await foreach (var page in client.Search.AggregatePagesAsync("idx", RespireSearchExpression.FromRaw("*")))
        {
            pages++;
            await Assert.That(hook.Completed.Count).IsEqualTo(pages);
            if (breakEarly) break;
        }

        await Assert.That(pages).IsEqualTo(breakEarly ? 1 : 2);
        await Assert.That(hook.Completed.Count).IsEqualTo(pages);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
        await Assert.That(server.ReceivedCommands.Contains("FT.CURSOR DEL idx 42")).IsEqualTo(breakEarly);
    }

    [Test]
    public async Task ReentrantApplicationCommandsHaveIndependentOwnersAndObserverFailureIsIgnored()
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        Task? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "FT.SEARCH") nested = client.Strings.GetStringAsync("nested").AsTask();
        };
        hook.After = _ => throw new InvalidOperationException("observer failure");

        await Call(client.Search, "search");
        await nested!;

        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Any(c => c.Command == "GET")).IsTrue();
        await Assert.That(hook.Completed.Any(c => c.Command == "FT.SEARCH")).IsTrue();
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecoratorReceivesMainDispatchAndOwnedProbe(bool synchronousFailure)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("FT.HYBRID", StringComparison.Ordinal) => "-ERR unknown command 'FT.HYBRID'\r\n"u8.ToArray(),
            "COMMAND INFO FT.HYBRID" => "*1\r\n$-1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, null));
        var decorated = DispatchProxy.Create<IRespireClient, ForwardingClient>();
        var proxy = (ForwardingClient)decorated;
        proxy.Inner = client;
        proxy.FailHybridSynchronously = synchronousFailure;

        var error = await Failure(() => Call(new RespireSearchClient(decorated), "hybrid"));

        await Assert.That(error).IsTypeOf<NotSupportedException>();
        await Assert.That(proxy.Commands.ToArray()).IsEquivalentTo(["FT.HYBRID", "COMMAND INFO"]);
    }

    [Test]
    [Arguments("cursor", false)]
    [Arguments("cursor", true)]
    [Arguments("read", false)]
    [Arguments("read", true)]
    public async Task DecoratorCleanupFailurePreservesMalformedPageError(string route, bool synchronousFailure)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) || command.StartsWith("FT.CURSOR READ", StringComparison.Ordinal)
                => "*2\r\n+bad\r\n:42\r\n"u8.ToArray(),
            "FT.CURSOR DEL idx 42" => FakeRespServer.OkReply,
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, null));
        var expected = await Failure(() => Call(client.Search, route));
        var decorated = DispatchProxy.Create<IRespireClient, ForwardingClient>();
        var proxy = (ForwardingClient)decorated;
        proxy.Inner = client;
        proxy.CursorCleanupError = new InvalidOperationException("decorator rejects cleanup");
        proxy.FailCleanupSynchronously = synchronousFailure;

        var error = await Failure(() => Call(new RespireSearchClient(decorated), route));

        await Assert.That(error.GetType()).IsEqualTo(expected.GetType());
        await Assert.That(error.Message).IsEqualTo(expected.Message);
        await Assert.That(proxy.Commands.ToArray()).IsEquivalentTo(
            [route == "cursor" ? "FT.AGGREGATE" : "FT.CURSOR READ", "FT.CURSOR DEL"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecoratorCleanupFailureDoesNotFailEarlyEnumerationExit(bool synchronousFailure)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal)
            ? "*2\r\n*1\r\n:0\r\n:42\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(Options(server, null));
        var decorated = DispatchProxy.Create<IRespireClient, ForwardingClient>();
        var proxy = (ForwardingClient)decorated;
        proxy.Inner = client;
        proxy.CursorCleanupError = new InvalidOperationException("decorator rejects cleanup");
        proxy.FailCleanupSynchronously = synchronousFailure;

        var pages = 0;
        await foreach (var page in new RespireSearchClient(decorated).AggregatePagesAsync("idx", RespireSearchExpression.FromRaw("*")))
        {
            pages++;
            break;
        }

        await Assert.That(pages).IsEqualTo(1);
        await Assert.That(proxy.Commands.ToArray()).IsEquivalentTo(["FT.AGGREGATE", "FT.CURSOR DEL"]);
        var error = await Failure(() => new RespireSearchClient(decorated).DeleteCursorAsync("idx", 42).AsTask());
        await Assert.That(error).IsSameReferenceAs(proxy.CursorCleanupError);
    }

    public class ForwardingClient : DispatchProxy
    {
        public IRespireClient Inner = null!;
        public bool FailHybridSynchronously;
        public Exception? CursorCleanupError;
        public bool FailCleanupSynchronously;
        public readonly List<string> Commands = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IRespireClient.ExecuteLeaseAsync))
            {
                Commands.Add(((RespireCommand)args![0]!).Name);
                if (FailHybridSynchronously && Commands[^1] == "FT.HYBRID")
                    throw new RespireServerException("ERR unknown command 'FT.HYBRID'");
                if (CursorCleanupError is { } cleanupError && Commands[^1] == "FT.CURSOR DEL")
                {
                    if (FailCleanupSynchronously) throw cleanupError;
                    return ValueTask.FromException<RespireResult>(cleanupError);
                }
            }
            return targetMethod.Invoke(Inner, args);
        }
    }

    private static async Task Call(RespireSearchClient search, string route, CancellationToken token = default)
    {
        var expression = RespireSearchExpression.FromRaw("*");
        switch (route)
        {
            case "search": await search.SearchAsync("idx", new(expression), token); break;
            case "aggregate": await search.AggregateAsync("idx", expression, cancellationToken: token); break;
            case "cursor": await search.AggregateWithCursorAsync("idx", expression, cancellationToken: token); break;
            case "read": await search.ReadCursorAsync("idx", 7, cancellationToken: token); break;
            case "delete": await search.DeleteCursorAsync("idx", 7, token); break;
            case "hybrid": await search.HybridSearchAsync("idx", new(expression, "embedding", new byte[] { 1, 2 }, 3), token); break;
            case "profile": await search.ProfileSearchAsync("idx", new(expression), cancellationToken: token); break;
            case "config": await search.GetConfigurationAsync(cancellationToken: token); break;
            case "config-set": await search.SetConfigurationAsync("TIMEOUT", "1", token); break;
            case "info": await search.GetIndexInfoAsync("idx", token); break;
            case "aliases": await search.ListAliasesAsync("idx", token); break;
            case "suggestion": await search.AddSuggestionAsync("suggestions", "term", 1, cancellationToken: token); break;
            case "synonyms": await search.GetSynonymsAsync("idx", token); break;
            case "spell": await search.SpellCheckAsync("idx", "word", cancellationToken: token); break;
            case "dictionary": await search.AddDictionaryTermsAsync("dict", ["word"], token); break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static FakeRespServer Server() => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command.StartsWith("FT.SEARCH", StringComparison.Ordinal)
            ? "*1\r\n:0\r\n"u8.ToArray() : command.StartsWith("GET ", StringComparison.Ordinal) ? "$1\r\nx\r\n"u8.ToArray() : null,
    };

    private static RespireOptions Options(FakeRespServer server, RespireCommandHook? hook) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5),
        CommandHook = hook, AllowAdmin = true,
    };

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private sealed class Hook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal Action<RespireCommandContext>? After;
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(string Command, object? State, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context.Command, state, outcome, error));
            After?.Invoke(context);
        }
    }
}
