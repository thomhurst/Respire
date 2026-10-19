# Respire.AI

Semantic response caching for Microsoft.Extensions.AI chat clients, backed by Redis vector search.

Also includes bounded Redis conversation history, `UseRespireChatHistory` middleware for
streaming and non-streaming calls, and `RespireChatHistoryProvider` for Microsoft Agent
Framework. History supports tool calls and results, atomic concurrent appends, trimming,
expiry, and clearing; it does not require Redis Search.

[Chat history guide](https://respire.tomlonghurst.dev/docs/guides/chat-history).

When composing history with tools, register `UseRespireChatHistory` before
`UseFunctionInvocation` so history wraps the whole tool loop. Reversing this order is
unsupported and duplicates messages during tool iterations. Pass only new messages;
repeated system messages are stored again too.

## Install

```sh
dotnet add package Respire.AI
```

## Usage

<!-- doc-test-declaration -->
```csharp
using Microsoft.Extensions.AI;
using Respire;
using Respire.AI;

public static class ChatSetup
{
    public static IChatClient Build(
        IChatClient provider,
        IRespireClient redis,
        IEmbeddingGenerator<string, Embedding<float>> embeddings)
        => new ChatClientBuilder(provider)
            .UseRespireSemanticCache(redis, embeddings, new()
            {
                CacheName = "support:model-v1:deployment-v1",
                Dimensions = 1536,
                DistanceThreshold = 0.1,
                TimeToLive = TimeSpan.FromMinutes(30),
                ScopeSelector = (_, options) =>
                    options?.AdditionalProperties?.TryGetValue("UserId", out var user) == true
                        ? user as string : null,
            })
            .Build();
}
```

Supply your chat provider and embedding generator, and match Dimensions to its output. Requires standalone Redis Query Engine. Set UserId in request AdditionalProperties to isolate users; see the guide for scope, bypass, and cache lifetime behavior.

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/semantic-cache) for configuration, requirements, and further examples.
