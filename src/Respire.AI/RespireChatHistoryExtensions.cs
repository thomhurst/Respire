using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
            var snapshots = new List<ChatResponseUpdate>();
            ExceptionDispatchInfo? snapshotError = null;

            await foreach (var update in InnerClient.GetStreamingResponseAsync(history.Concat(request), options, cancellationToken).ConfigureAwait(false))
            {
                if (snapshotError is null)
                {
                    try
                    {
                        snapshots.Add(SnapshotUpdate(update));
                    }
                    catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
                    {
                        // A history serialization error belongs to the completed turn, not to delivery of a chunk.
                        snapshotError = ExceptionDispatchInfo.Capture(error);
                    }
                }

                yield return update;
            }

            // Deliberately save only complete turns; early disposal or failure must not persist partial tool exchanges.
            cancellationToken.ThrowIfCancellationRequested();
            snapshotError?.Throw();
            var response = snapshots.ToChatResponse();
            await store.AppendAsync(conversationId, request.Concat(response.Messages), cancellationToken).ConfigureAwait(false);
        }

        private static ChatResponseUpdate SnapshotUpdate(ChatResponseUpdate update)
        {
            var snapshot = new ChatResponseUpdate
            {
                Role = update.Role,
                AuthorName = update.AuthorName,
                MessageId = update.MessageId,
                CreatedAt = update.CreatedAt,
            };

            // Updates without a message ID carry response-level properties, which the store doesn't persist.
            if (update.MessageId is { Length: > 0 } && update.AdditionalProperties is { } properties)
            {
                snapshot.AdditionalProperties = SnapshotJson(properties);
            }

            foreach (var content in update.Contents)
            {
                if (content is null or UsageContent)
                {
                    continue;
                }

                snapshot.Contents.Add(SnapshotContent(content));
            }

            return snapshot;
        }

        private static AIContent SnapshotContent(AIContent content)
        {
            if (content is TextContent { AdditionalProperties: null, Annotations: null } plainText
                && content.GetType() == typeof(TextContent))
            {
                return new TextContent(plainText.Text);
            }

            // The store's JSON contract copies nested content such as tool arguments and data buffers.
            var snapshot = SnapshotJson(content);

            // Adjacent chunks can split a UTF-16 surrogate pair. Keep the original text until aggregation.
            if (content is TextContent text && snapshot is TextContent copiedText)
            {
                copiedText.Text = text.Text;
            }
            else if (content is TextReasoningContent reasoning && snapshot is TextReasoningContent copiedReasoning)
            {
                copiedReasoning.Text = reasoning.Text;
            }

            return snapshot;
        }

        private static T SnapshotJson<T>(T value) where T : class
        {
            var typeInfo = (JsonTypeInfo<T>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));
            var json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
            return JsonSerializer.Deserialize(json, typeInfo) ?? throw new JsonException("A chat history snapshot is null.");
        }

        private static List<ChatMessage> Materialize(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            ArgumentNullException.ThrowIfNull(messages);

            if (options?.ConversationId is not null || options?.ContinuationToken is not null || options?.AllowBackgroundResponses == true)
            {
                throw new ArgumentException("Redis chat history requires client-managed, foreground conversation history.", nameof(options));
            }

            return messages.ToList();
        }
    }
}
