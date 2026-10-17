---
title: Chat history and agent memory
description: Persist Microsoft.Extensions.AI conversations and Microsoft Agent Framework history in Redis.
---

# Chat history and agent memory

:::warning Experimental API: RSP0002
`Respire.AI` requires an explicit compiler opt-in and is outside the 2.0 compatibility promise.
See [RSP0002](../diagnostics/RSP0002.md).
:::

`RespireChatHistoryStore` persists `Microsoft.Extensions.AI.ChatMessage` values in Redis
lists. It works with RESP2 and RESP3 on .NET 8 and .NET 10 and does not require Redis Search.
Each operation addresses one key, including when routing through Redis Cluster.

## Conversation store

```sh
dotnet add package Respire.AI
```

<!-- doc-test-declaration -->
```csharp
using Microsoft.Extensions.AI;
using Respire;
using Respire.AI;

public static class ConversationSetup
{
    public static IChatClient Build(IChatClient provider, IRespireClient redis, string conversationId)
    {
        var history = new RespireChatHistoryStore(redis, new()
        {
            StoreName = "support:tenant-42",
            MaximumMessages = 1000,
            TimeToLive = TimeSpan.FromDays(1),
        });
        return new ChatClientBuilder(provider)
            .UseRespireChatHistory(history, conversationId, maximumMessages: 100)
            .Build();
    }
}
```

Supply a stable, trusted conversation ID and an application/tenant `StoreName`. Both are
hashed separately into `respire:history:v1:<store-hash>:<conversation-hash>`. Hashing
avoids delimiter collisions; it does not authorize access or encrypt stored messages.
The caller owns the Redis client. Disposing a chat pipeline disposes its inner provider.

Use the store directly with `AppendAsync`, `ReadAsync`, `TrimAsync`, `ExpireAsync`, and
`ClearAsync`. Reads return the newest requested messages in chronological order. The
default read window is 100 messages and the default storage limit is 1000 messages.
`TrimAsync(id, 0)` clears a conversation. Missing conversations read as empty, and
`ExpireAsync` returns false when the key does not exist.

Appends serialize every message before writing. A Lua script then appends the batch,
keeps the newest `MaximumMessages`, and sets the idle TTL atomically. Concurrent writers
cannot interleave messages within a batch or lose each other's appends. Batches appear
in Redis execution order. The default idle TTL is one day. Nonempty appends renew it;
reads, trims, and empty appends do not. `ExpireAsync` sets a positive remaining lifetime;
the next nonempty append restores the configured idle TTL. Lifetimes use whole
milliseconds and must be at least one millisecond.

JSON uses `AIJsonUtilities.DefaultOptions`, including the built-in polymorphic tool call
and tool result content. Provider-specific raw representations are not durable history.
Custom content must be supported by those JSON options; serialization errors propagate
before any messages are appended. Stored message count and read windows are message
limits, not tokenizer budgets. Choose windows at valid conversation boundaries when
using tools: arbitrary trimming can separate a tool call from its result or remove a
system instruction. Supply persistent policy instructions separately when needed.

## Chat middleware

Pass only **new messages** on each call. The middleware prepends saved history, invokes
the provider, and atomically appends the new request and response after success. Passing
the entire transcript again duplicates it. Configure the provider for client-managed
history; request options with `ConversationId`, `ContinuationToken`, or background
responses are rejected.

System messages supplied on every call are also appended every time. Send them once
per conversation while retained, or inject persistent policy instructions through
separate middleware inside the history middleware.

Streaming updates are forwarded as they arrive and aggregated with `ToChatResponse`.
History is saved only when the stream completes successfully and is fully enumerated.
Cancellation, provider failure, and early enumeration disposal do not save a partial
turn. Redis failures propagate, including a save failure after the provider has already
answered. Storage writes and model calls are not a distributed transaction. Retrying
after an ambiguous network failure can duplicate a turn.

Serialize model calls for the same conversation in your application. Atomic appends do
not make concurrent model calls read each other's unfinished turns. Use one pipeline
per conversation, or construct a pipeline per request with that request's trusted ID.
When using tools, register `UseRespireChatHistory` **before** `UseFunctionInvocation`.
Builder registration order places history outside the tool loop, so it loads once and
saves the entire completed turn once:

<!-- doc-test-declaration -->
```csharp
using Microsoft.Extensions.AI;
using Respire.AI;

public static class ToolConversationSetup
{
    public static IChatClient Build(IChatClient provider, RespireChatHistoryStore history, string conversationId)
        => new ChatClientBuilder(provider)
            .UseRespireChatHistory(history, conversationId)
            .UseFunctionInvocation()
            .Build();
}
```

The reversed order is unsupported:

<!-- doc-test-ignore: Deliberately unsupported ordering; provider, history, and conversationId come from the example above. -->
```csharp
// Wrong: history runs separately for each tool iteration.
new ChatClientBuilder(provider)
    .UseFunctionInvocation()
    .UseRespireChatHistory(history, conversationId)
    .Build();
```

In that order, each iteration reloads history and saves an overlapping request and
response. Messages are duplicated in Redis and replayed to the provider. The middleware
does not detect or repair this ordering; use the required registration order above.

The [runnable sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.ChatHistory)
shows two turns across separate pipelines using Redis and a deterministic demo provider.

## Microsoft Agent Framework and Semantic Kernel

Microsoft Agent Framework exposes
[`ChatHistoryProvider`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.chathistoryprovider).
`RespireChatHistoryProvider` implements its load/store hooks. Construct it with the store
and a `Func<AgentSession, string>` that resolves a stable conversation ID from the session
or application mapping. Use it as the agent's history provider. The base provider merges
loaded messages with the request and filters already loaded history from subsequent
writes. Failed invocations do not append messages. Different sessions must resolve to
different IDs unless they intentionally share a conversation. Do not also enable
`UseRespireChatHistory` for that agent.

Semantic Kernel's
[`ChatHistory`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.semantickernel.chatcompletion.chathistory)
is a collection of `ChatMessageContent`, rather than a common persistence interface for
`Microsoft.Extensions.AI.ChatMessage`. Applications using Semantic Kernel directly must
convert their messages explicitly; this package does not silently discard content in a
text-only conversion.

## Optional long-term facts

Conversation history is an ordered transcript. For semantic recall of durable facts,
use a separate [Respire.VectorData collection](vector-data.md) with an embedding field,
fact text, and a tenant/user identifier. Generate embeddings with your model, upsert
selected facts, then search with a tenant/user filter and prepend the retrieved facts
as application context. Keep that collection's retention and deletion policy separate
from the transcript. Fact extraction and automatic promotion of chat messages are
application choices; the history middleware does not run another model or store
embeddings automatically.
