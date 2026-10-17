using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Respire.AI;

/// <summary>Adapts Redis conversation history to Microsoft Agent Framework sessions.</summary>
/// <remarks>The selector must return a stable application/tenant-scoped conversation ID for each session.
/// The provider does not own the store. Do not also use the chat history middleware on the same agent.</remarks>
public sealed class RespireChatHistoryProvider : ChatHistoryProvider
{
    private readonly RespireChatHistoryStore _store;
    private readonly Func<AgentSession, string> _conversationIdSelector;
    private readonly int _maximumMessages;

    /// <summary>Creates a provider whose session selector supplies the Redis conversation ID.</summary>
    public RespireChatHistoryProvider(RespireChatHistoryStore store, Func<AgentSession, string> conversationIdSelector, int maximumMessages = 100)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(conversationIdSelector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessages);
        _store = store;
        _conversationIdSelector = conversationIdSelector;
        _maximumMessages = maximumMessages;
    }

    /// <summary>Loads messages for the current session.</summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
        => await _store.ReadAsync(GetConversationId(context.Session), _maximumMessages, cancellationToken).ConfigureAwait(false);

    /// <summary>Stores the filtered request and response messages only after a successful invocation.</summary>
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null) return;
        await _store.AppendAsync(GetConversationId(context.Session), context.RequestMessages.Concat(context.ResponseMessages ?? []), cancellationToken).ConfigureAwait(false);
    }

    private string GetConversationId(AgentSession? session)
        => _conversationIdSelector(session ?? throw new InvalidOperationException("Redis chat history requires an agent session."));
}
