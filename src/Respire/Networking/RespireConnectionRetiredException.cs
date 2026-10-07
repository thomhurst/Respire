namespace Respire.Networking;

/// <summary>The transport retired before accepting this command. No bytes were enqueued.</summary>
/// <remarks>
/// <para>Routers may retry this failure on a current generation. It never represents an
/// accepted command, a server error, or an ambiguous connection failure (unlike
/// <see cref="RespireConnectionException"/>). Never retry an accepted command merely because
/// topology changed. Cluster topology publication detaches old generations before starting
/// their graceful retirement.</para>
/// <para>Cluster command, tracked-read, fire-and-forget, script, lock, batch, and unwatched
/// transaction paths retry this rejection within the existing bounded redirect budget. ASK
/// retries keep the temporary target and ASKING prefix without changing the slot owner. Tracked
/// executions publish the replacement identity before writing; cached reads refresh their
/// continuity token. An accepted batch entry is never replayed because another entry was rejected.</para>
/// <para>Route acquisition also retries this before returning a connection, a dedicated pool, or a
/// primary snapshot. Retirement-owned cancellation of an unpublished handshake is distinguished
/// from caller cancellation. Typed FUNCTION/SCRIPT mutations, flush/size fan-outs, SCAN pages, and
/// server-node discovery retry only the rejected endpoint and never replay accepted peers.
/// Socket-pinned CLIENT operations and existing WATCH state do not use endpoint replacement.
/// SCAN keeps weak iteration semantics; its cursor does not survive resharding. Dedicated rentals
/// retry through <see cref="Respire.Internal.DedicatedLeaseAcquisition"/>.</para>
/// </remarks>
internal sealed class RespireConnectionRetiredException(string host, int port)
    : RespireException($"Connection to {host}:{port} is retired; the command was not accepted.")
{
    internal RespireEndpoint Endpoint { get; } = new(host, port);
}
