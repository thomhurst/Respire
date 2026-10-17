using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Respire.IntegrationTests;
using TUnit.Core;

#pragma warning disable MAAI001 // Construct framework invocation contexts to test the provider contract directly.

namespace Respire.AI.Tests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ChatHistoryTests(ModernRedisTestContainer fixture)
{
    [Test, Arguments(2), Arguments(3)]
    public async Task RoundTripsMessagesAndToolCallsInOrder(int protocol)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis);
        await store.AppendAsync("chat", [new(ChatRole.System, "policy"), new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "weather", new Dictionary<string, object?> { ["city"] = "London" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "rain")])]);
        var messages = await store.ReadAsync("chat");
        messages.Select(m => m.Role).Should().Equal(ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool);
        var call = messages[2].Contents.Should().ContainSingle().Subject.Should().BeOfType<FunctionCallContent>().Subject;
        call.CallId.Should().Be("call-1");
        call.Name.Should().Be("weather");
        call.Arguments!["city"]!.ToString().Should().Be("London");
        messages[3].Contents.Single().Should().BeOfType<FunctionResultContent>().Which.CallId.Should().Be("call-1");
        (await store.ReadAsync("chat", 2)).Select(m => m.Role).Should().Equal(ChatRole.Assistant, ChatRole.Tool);
        // Count windows preserve the requested transcript slice, even across tool boundaries.
        (await store.ReadAsync("chat", 1)).Select(m => m.Role).Should().Equal(ChatRole.Tool);
        await store.TrimAsync("chat", 1);
        (await store.ReadAsync("chat")).Select(m => m.Role).Should().Equal(ChatRole.Tool);
        (await store.ReadAsync("other")).Should().BeEmpty();
        (await Store(redis).ReadAsync("chat")).Should().BeEmpty();
        await store.ClearAsync("chat");
        (await store.ReadAsync("chat")).Should().BeEmpty();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task AppendAndExplicitTrimKeepNewestMessages(int protocol)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis, maximumMessages: 3);
        await store.AppendAsync("chat", Enumerable.Range(0, 6).Select(i => new ChatMessage(ChatRole.User, i.ToString())));
        (await store.ReadAsync("chat")).Select(m => m.Text).Should().Equal("3", "4", "5");
        await store.TrimAsync("chat", 2);
        (await store.ReadAsync("chat")).Select(m => m.Text).Should().Equal("4", "5");
        await store.TrimAsync("chat", 0);
        (await store.ReadAsync("chat")).Should().BeEmpty();
        await store.TrimAsync("missing", 1);
        (await store.ReadAsync("missing")).Should().BeEmpty();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task ConcurrentWritersKeepWholeBatchesContiguous(int protocol)
    {
        await using var redis = await Connect(protocol);
        var name = Guid.NewGuid().ToString("N");
        var first = new RespireChatHistoryStore(redis, new() { StoreName = name });
        await using var secondRedis = await Connect(protocol);
        var second = new RespireChatHistoryStore(secondRedis, new() { StoreName = name });
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            (i % 2 == 0 ? first : second).AppendAsync("chat", [new(ChatRole.User, i + ":request"), new(ChatRole.Assistant, i + ":response")]).AsTask()));
        var history = await first.ReadAsync("chat");
        history.Should().HaveCount(80);
        history.Select(m => m.Text).Distinct().Should().HaveCount(80);
        for (var i = 0; i < history.Count; i += 2)
            history[i + 1].Text.Should().Be(history[i].Text.Replace(":request", ":response", StringComparison.Ordinal));
        await first.ClearAsync("chat");
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task AppendRenewsTtlButReadsTrimsAndEmptyAppendsDoNot(int protocol)
    {
        await using var redis = await Connect(protocol);
        var name = Guid.NewGuid().ToString("N");
        var store = new RespireChatHistoryStore(redis, new() { StoreName = name, TimeToLive = TimeSpan.FromMinutes(5) });
        var key = "respire:history:v1:" + Hash(name) + ":" + Hash("chat");
        await store.AppendAsync("chat", [new(ChatRole.User, "hello")]);
        using (var ttl = await redis.ExecuteAsync("PTTL", [key])) ttl.AsInteger().Should().BeInRange(1, 300000);
        (await store.ExpireAsync("chat", TimeSpan.FromMinutes(1))).Should().BeTrue();
        using var expiry = await redis.ExecuteAsync("PEXPIRETIME", [key]);
        await store.ReadAsync("chat");
        await store.TrimAsync("chat", 1);
        await store.AppendAsync("chat", []);
        using (var unchanged = await redis.ExecuteAsync("PEXPIRETIME", [key])) unchanged.AsInteger().Should().Be(expiry.AsInteger());
        await store.AppendAsync("chat", [new(ChatRole.Assistant, "reply")]);
        using (var renewed = await redis.ExecuteAsync("PEXPIRETIME", [key])) renewed.AsInteger().Should().BeGreaterThan(expiry.AsInteger());
        // Force expiry on the server clock: verifies expired history is absent without a timing-sensitive delay.
        using (var expired = await redis.ExecuteAsync("PEXPIREAT", [key, 1])) expired.AsInteger().Should().Be(1);
        (await store.ReadAsync("chat")).Should().BeEmpty();
        (await store.ExpireAsync("missing", TimeSpan.FromMinutes(1))).Should().BeFalse();
        await store.AppendAsync("missing", []);
        (await store.ReadAsync("missing")).Should().BeEmpty();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task MiddlewareLoadsHistoryAndStoresOnlyNewTurns(int protocol)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis);
        var provider = new RecordingChatClient();
        using (var chat = new ChatClientBuilder(provider).UseRespireChatHistory(store, "chat").Build())
        {
            await chat.GetResponseAsync([new(ChatRole.User, "one")]);
            await chat.GetResponseAsync([new(ChatRole.User, "two")]);
            provider.LastRequest.Select(m => m.Text).Should().Equal("one", "reply", "two");
            (await store.ReadAsync("chat")).Select(m => m.Text).Should().Equal("one", "reply", "two", "reply");
            provider.Fail = true;
            Func<Task> failure = () => chat.GetResponseAsync([new(ChatRole.User, "failed")]);
            await failure.Should().ThrowAsync<InvalidOperationException>();
            (await store.ReadAsync("chat")).Should().HaveCount(4);
            Func<Task> stateful = () => chat.GetResponseAsync([new(ChatRole.User, "stateful")], new() { ConversationId = "server" });
            await stateful.Should().ThrowAsync<ArgumentException>();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Func<Task> cancellation = () => chat.GetResponseAsync([new(ChatRole.User, "cancelled")], cancellationToken: cancelled.Token);
            await cancellation.Should().ThrowAsync<OperationCanceledException>();
        }
        provider.Disposed.Should().BeTrue();
        // Disposing the pipeline must not dispose the Redis client.
        await store.ClearAsync("chat");
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task StreamingStoresOnlyAfterSuccessfulCompleteEnumeration(int protocol)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis);
        var provider = new RecordingChatClient();
        using var chat = new ChatClientBuilder(provider).UseRespireChatHistory(store, "chat").Build();
        await foreach (var _ in chat.GetStreamingResponseAsync([new(ChatRole.User, "abandoned")])) break;
        (await store.ReadAsync("chat")).Should().BeEmpty();
        await foreach (var _ in chat.GetStreamingResponseAsync([new(ChatRole.User, "complete")])) { }
        (await store.ReadAsync("chat")).Select(m => m.Text).Should().Equal("complete", "hello world");
        provider.Fail = true;
        Func<Task> failure = async () => { await foreach (var _ in chat.GetStreamingResponseAsync([new(ChatRole.User, "failed")])) { } };
        await failure.Should().ThrowAsync<InvalidOperationException>();
        (await store.ReadAsync("chat")).Should().HaveCount(2);
        await store.ClearAsync("chat");
    }

    [Test, Arguments(2, false), Arguments(3, false), Arguments(2, true), Arguments(3, true)]
    public async Task MiddlewarePersistsWholeToolLoopExactlyOnce(int protocol, bool streaming)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis);
        var provider = new ToolLoopChatClient();
        var invocations = 0;
        using var chat = new ChatClientBuilder(provider)
            .UseRespireChatHistory(store, "chat")
            .UseFunctionInvocation()
            .Build();
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => { invocations++; return "rain"; }, "weather")],
        };
        if (streaming)
        {
            await foreach (var _ in chat.GetStreamingResponseAsync([new(ChatRole.User, "weather?")], options)) { }
        }
        else
        {
            await chat.GetResponseAsync([new(ChatRole.User, "weather?")], options);
        }

        invocations.Should().Be(1);
        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].Select(m => m.Role).Should().Equal(ChatRole.User);
        provider.Requests[1].Select(m => m.Role).Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool);
        var saved = await store.ReadAsync("chat");
        saved.Select(m => m.Role).Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        saved[0].Text.Should().Be("weather?");
        var call = saved[1].Contents.Should().ContainSingle().Subject.Should().BeOfType<FunctionCallContent>().Subject;
        call.CallId.Should().Be("weather-call");
        call.Name.Should().Be("weather");
        var result = saved[2].Contents.Should().ContainSingle().Subject.Should().BeOfType<FunctionResultContent>().Subject;
        result.CallId.Should().Be("weather-call");
        result.Result!.ToString().Should().Be("rain");
        saved[3].Text.Should().Be("Bring an umbrella.");

        // A new pipeline must load the complete tool exchange from Redis, in either response mode.
        using var next = new ChatClientBuilder(provider).UseRespireChatHistory(store, "chat").UseFunctionInvocation().Build();
        if (streaming)
        {
            await foreach (var _ in next.GetStreamingResponseAsync([new(ChatRole.User, "thanks")], options)) { }
        }
        else
        {
            await next.GetResponseAsync([new(ChatRole.User, "thanks")], options);
        }

        invocations.Should().Be(1);
        provider.Requests.Should().HaveCount(3);
        provider.Requests[2].Select(m => m.Role).Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User);
        provider.Requests[2].Take(4).Select(SerializeMessage).Should().Equal(saved.Select(SerializeMessage));
        provider.Requests[2][4].Text.Should().Be("thanks");
        var final = await store.ReadAsync("chat");
        final.Should().HaveCount(6);
        final.Take(4).Select(SerializeMessage).Should().Equal(saved.Select(SerializeMessage));
        final.Skip(4).Select(m => m.Text).Should().Equal("thanks", "You're welcome.");
        await store.ClearAsync("chat");
    }

    [Test, Arguments(2, false), Arguments(3, false), Arguments(2, true), Arguments(3, true)]
    public async Task ReversedMiddlewareOrderDuplicatesToolLoopMessages(int protocol, bool streaming)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis);
        var provider = new ToolLoopChatClient();
        // Unsupported order: demonstrates why history must wrap function invocation.
        using var chat = new ChatClientBuilder(provider)
            .UseFunctionInvocation()
            .UseRespireChatHistory(store, "chat")
            .Build();
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "rain", "weather")],
        };
        if (streaming)
        {
            await foreach (var _ in chat.GetStreamingResponseAsync([new(ChatRole.User, "weather?")], options)) { }
        }
        else
        {
            await chat.GetResponseAsync([new(ChatRole.User, "weather?")], options);
        }

        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Select(m => m.Role).Should().Equal(
            ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.Tool);
        var saved = await store.ReadAsync("chat");
        saved.Select(m => m.Role).Should().Equal(
            ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        saved[0].Text.Should().Be("weather?");
        saved[2].Text.Should().Be("weather?");
        saved[5].Text.Should().Be("Bring an umbrella.");
        await store.ClearAsync("chat");
    }

    private static string SerializeMessage(ChatMessage message)
        => JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatMessage)));

    [Test, Arguments(2), Arguments(3)]
    public async Task AgentProviderUsesSessionIdentityAndDoesNotDuplicateLoadedHistory(int protocol)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis);
        var provider = new RespireChatHistoryProvider(store, session => session.StateBag.GetValue<string>("conversation")!);
        var session = new TestSession();
        session.StateBag.SetValue("conversation", "one");
        var agent = new TestAgent();
        await store.AppendAsync("one", [new(ChatRole.User, "old")]);
        var request = (await provider.InvokingAsync(new(agent, session, [new(ChatRole.User, "new")]))).ToList();
        request.Select(m => m.Text).Should().Equal("old", "new");
        await provider.InvokedAsync(new(agent, session, request, new[] { new ChatMessage(ChatRole.Assistant, "reply") }));
        (await store.ReadAsync("one")).Select(m => m.Text).Should().Equal("old", "new", "reply");
        await provider.InvokedAsync(new(agent, session, request, new InvalidOperationException("failed")));
        (await store.ReadAsync("one")).Should().HaveCount(3);
        session.StateBag.SetValue("conversation", "two");
        (await provider.InvokingAsync(new(agent, session, []))).Should().BeEmpty();
        await store.ClearAsync("one");
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task InvalidBatchAndCancellationLeaveHistoryUnchanged(int protocol)
    {
        await using var redis = await Connect(protocol);
        var store = Store(redis);
        Func<Task> invalidBatch = () => store.AppendAsync("chat", [new(ChatRole.User, "must not be saved"), null!]).AsTask();
        await invalidBatch.Should().ThrowAsync<ArgumentNullException>();
        var cyclicResult = new Dictionary<string, object?>();
        cyclicResult["self"] = cyclicResult;
        Func<Task> invalidJson = () => store.AppendAsync("chat", [new(ChatRole.User, "must not be saved"),
            new(ChatRole.Tool, [new FunctionResultContent("call", cyclicResult)])]).AsTask();
        await invalidJson.Should().ThrowAsync<JsonException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> cancellation = () => store.AppendAsync("chat", [new(ChatRole.User, "cancelled")], cancelled.Token).AsTask();
        await cancellation.Should().ThrowAsync<OperationCanceledException>();
        (await store.ReadAsync("chat")).Should().BeEmpty();
        Action invalidLimit = () => _ = new RespireChatHistoryStore(redis, new() { MaximumMessages = 0 });
        invalidLimit.Should().Throw<ArgumentOutOfRangeException>();
        Action invalidTtl = () => _ = new RespireChatHistoryStore(redis, new() { TimeToLive = TimeSpan.FromTicks(1) });
        invalidTtl.Should().Throw<ArgumentOutOfRangeException>();
        Func<Task> invalidRead = () => store.ReadAsync("chat", 0).AsTask();
        await invalidRead.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Func<Task> invalidTrim = () => store.TrimAsync("chat", -1).AsTask();
        await invalidTrim.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Func<Task> invalidExpiry = () => store.ExpireAsync("chat", TimeSpan.Zero).AsTask();
        await invalidExpiry.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Func<Task> invalidId = () => store.ReadAsync(" ").AsTask();
        await invalidId.Should().ThrowAsync<ArgumentException>();
    }

    private ValueTask<RespireClient> Connect(int protocol)
        => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });

    private static RespireChatHistoryStore Store(IRespireClient client, int maximumMessages = 1000)
        => new(client, new() { StoreName = Guid.NewGuid().ToString("N"), MaximumMessages = maximumMessages });

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class TestSession : AgentSession;

    private sealed class TestAgent : AIAgent
    {
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new TestSession());
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ToolLoopChatClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(Respond(messages)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = Respond(messages);
            yield return new ChatResponseUpdate(response.Role, response.Contents) { MessageId = "message-" + Requests.Count };
            await Task.CompletedTask;
        }

        private ChatMessage Respond(IEnumerable<ChatMessage> messages)
        {
            Requests.Add(messages.ToList());
            return Requests.Count switch
            {
                1 => new(ChatRole.Assistant, [new FunctionCallContent("weather-call", "weather", new Dictionary<string, object?>())]),
                2 => new(ChatRole.Assistant, "Bring an umbrella."),
                _ => new(ChatRole.Assistant, "You're welcome."),
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> LastRequest { get; private set; } = [];
        public bool Fail { get; set; }
        public bool Disposed { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastRequest = messages.ToList();
            if (Fail) throw new InvalidOperationException("provider failed");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = messages.ToList();
            yield return new(ChatRole.Assistant, "hello");
            await Task.CompletedTask;
            if (Fail) throw new InvalidOperationException("provider failed");
            yield return new(ChatRole.Assistant, " world");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}
