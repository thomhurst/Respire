using Microsoft.Extensions.AI;
using Respire;
using Respire.AI;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var redis = await RespireClient.ConnectAsync(args.FirstOrDefault() ?? "redis://localhost:6379", deadline.Token);
var store = new RespireChatHistoryStore(redis, new()
{
    StoreName = "sample:chat-history",
    MaximumMessages = 100,
    TimeToLive = TimeSpan.FromMinutes(30),
});
var conversationId = "demo:" + Guid.NewGuid().ToString("N");
try
{
    // Replace DemoChatClient with your Microsoft.Extensions.AI provider in a real application.
    using (var chat = new ChatClientBuilder(new DemoChatClient()).UseRespireChatHistory(store, conversationId).Build())
    {
        var first = await chat.GetResponseAsync([new(ChatRole.User, "My name is Ada.")], cancellationToken: deadline.Token);
        Console.WriteLine(first.Text);
    }
    // A new pipeline uses the same Redis conversation, as after an application restart.
    using (var chat = new ChatClientBuilder(new DemoChatClient()).UseRespireChatHistory(store, conversationId).Build())
    {
        var second = await chat.GetResponseAsync([new(ChatRole.User, "What is my name?")], cancellationToken: deadline.Token);
        if (second.Text != "Your name is Ada.") throw new InvalidOperationException("Redis chat history was not loaded.");
        Console.WriteLine(second.Text);
    }
    if ((await store.ReadAsync(conversationId, cancellationToken: deadline.Token)).Count != 4)
        throw new InvalidOperationException("The two turns were not persisted.");
    Console.WriteLine("Redis multi-turn history sample passed.");
}
finally
{
    await store.ClearAsync(conversationId, CancellationToken.None);
}

internal sealed class DemoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var history = messages.ToList();
        var answer = history[^1].Text == "What is my name?" && history.Any(m => m.Role == ChatRole.User && m.Text == "My name is Ada.")
            ? "Your name is Ada." : "I will remember your name.";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This demo uses non-streaming responses.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
