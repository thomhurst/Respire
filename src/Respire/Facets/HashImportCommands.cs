namespace Respire;

public partial interface IHashCommands
{
    /// <summary>Opens a dedicated Redis 8.10 hash-import connection. Cluster callers must supply a routing key.</summary>
    ValueTask<RespireHashImportSession> CreateImportSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a dedicated Redis 8.10 hash-import connection for the routing key's effective Cluster slot.</summary>
    /// <remarks>The routing key is not read or written. Every imported key must use the same effective slot.</remarks>
    ValueTask<RespireHashImportSession> CreateImportSessionAsync(RespireKey routingKey, CancellationToken cancellationToken = default);
}

internal sealed partial class HashCommands
{
    public ValueTask<RespireHashImportSession> CreateImportSessionAsync(CancellationToken cancellationToken = default)
        => RespireHashImportSession.OpenAsync(client, null, cancellationToken);

    public ValueTask<RespireHashImportSession> CreateImportSessionAsync(RespireKey routingKey, CancellationToken cancellationToken = default)
        => RespireHashImportSession.OpenAsync(client, routingKey, cancellationToken);
}
