using System.Diagnostics.CodeAnalysis;

namespace Respire;

public partial interface IRespireHashCommands
{
    /// <summary>Opens a dedicated Redis 8.10 hash-import connection. Cluster callers must supply a routing key.</summary>
    /// <remarks>The command hook admits HIMPORT OPEN before connection setup and completes when session ownership transfers.</remarks>
    [Experimental("RSP0004", UrlFormat = "https://thomhurst.github.io/Respire/docs/diagnostics/{0}")]
    ValueTask<RespireHashImportSession> CreateImportSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a dedicated Redis 8.10 hash-import connection for the routing key's effective Cluster slot.</summary>
    /// <remarks>The routing key is not read or written. Every imported key must use the same effective slot.
    /// The command hook admits HIMPORT OPEN before connection setup and completes when session ownership transfers.</remarks>
    [Experimental("RSP0004", UrlFormat = "https://thomhurst.github.io/Respire/docs/diagnostics/{0}")]
    ValueTask<RespireHashImportSession> CreateImportSessionAsync(RespireKey routingKey, CancellationToken cancellationToken = default);
}

internal sealed partial class HashCommands
{
    public ValueTask<RespireHashImportSession> CreateImportSessionAsync(CancellationToken cancellationToken = default)
        => RespireHashImportSession.OpenAsync(client, null, cancellationToken);

    public ValueTask<RespireHashImportSession> CreateImportSessionAsync(RespireKey routingKey, CancellationToken cancellationToken = default)
        => RespireHashImportSession.OpenAsync(client, routingKey, cancellationToken);
}
