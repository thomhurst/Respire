using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Respire.AI;

/// <summary>Adds Redis conversation history to chat clients.</summary>
public static class RespireChatHistoryExtensions
{
    /// <summary>Loads history before each call and stores new request/response messages after success.</summary>
    /// <remarks>Pass only new messages. Serialize calls for the same conversation in the application.
    /// Register this middleware before UseFunctionInvocation so history wraps the entire tool loop.
    /// Reversing that order is unsupported: each tool iteration reloads and saves overlapping messages.
    /// Repeated system messages are stored again; send them once per conversation while retained.
    /// Streaming stores messages only after complete enumeration. Storage and provider errors propagate.
    /// The caller owns the store and its Redis client; the pipeline owns the inner chat client.</remarks>
    public static ChatClientBuilder UseRespireChatHistory(this ChatClientBuilder builder, RespireChatHistoryStore store,
        string conversationId, int maximumMessages = 100)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessages);
        return builder.Use(inner => new HistoryChatClient(inner, store, conversationId, maximumMessages));
    }

    private sealed class HistoryChatClient(IChatClient inner, RespireChatHistoryStore store, string conversationId, int maximumMessages)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = Materialize(messages, options);
            var history = await store.ReadAsync(conversationId, maximumMessages, cancellationToken).ConfigureAwait(false);
            var response = await InnerClient.GetResponseAsync(history.Concat(request), options, cancellationToken).ConfigureAwait(false);
            await store.AppendAsync(conversationId, request.Concat(response.Messages), cancellationToken).ConfigureAwait(false);
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Materialize(messages, options);
            var history = await store.ReadAsync(conversationId, maximumMessages, cancellationToken).ConfigureAwait(false);
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in InnerClient.GetStreamingResponseAsync(history.Concat(request), options, cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
                yield return update;
            }
            // Deliberately save only complete turns; early disposal or failure must not persist partial tool exchanges.
            var response = updates.ToChatResponse();
            await store.AppendAsync(conversationId, request.Concat(response.Messages), cancellationToken).ConfigureAwait(false);
        }

        private static List<ChatMessage> Materialize(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            ArgumentNullException.ThrowIfNull(messages);
            if (options?.ConversationId is not null || options?.ContinuationToken is not null || options?.AllowBackgroundResponses == true)
                throw new ArgumentException("Redis chat history requires client-managed, foreground conversation history.", nameof(options));
            return messages.ToList();
        }
    }
}
