using Respire.Protocol;

namespace Respire.Networking;

// An optional owner for connections whose endpoint can cease to be authoritative.
// Notifications run on transport threads and must not invoke user callbacks or block.
internal interface IConnectionGeneration
{
    bool IsRetired { get; }
    ValueTask ValidateAsync(RespireConnection connection, CancellationToken cancellationToken);
    void ObserveResponse(RespireConnection connection, string? operation, in RespValue response);
    void ConnectionClosed(RespireConnection connection, bool unexpected);
}
