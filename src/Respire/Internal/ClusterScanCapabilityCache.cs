using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal sealed class ClusterScanCapabilityCache
{
    private readonly ConditionalWeakTable<RespireConnection, Capability> _connections = new();
    private sealed record Evidence(string RunId, bool Supported);
    private sealed class Capability { internal Evidence? Evidence; }

    // Unknown metadata is memoized only within one page's recovery. A later page
    // can observe changed ACLs without treating denied metadata as command absence.
    internal sealed class ProbeRound
    {
        private readonly Dictionary<RespireConnection, string> _unknown = new();
        internal bool IsUnknown(RespireConnection connection, string runId)
            => _unknown.TryGetValue(connection, out var knownRun) && knownRun == runId;
        internal void RecordUnknown(RespireConnection connection, string runId) => _unknown[connection] = runId;
    }

    internal void RecordAbsent(RespireConnection connection, string runId)
        => Volatile.Write(ref _connections.GetOrCreateValue(connection).Evidence, new(runId, false));

    internal async ValueTask<bool> SupportsAsync(RespireClient client, RespireConnection connection,
        string runId, ProbeRound round, CancellationToken cancellationToken)
    {
        var capability = _connections.GetOrCreateValue(connection);
        if (Volatile.Read(ref capability.Evidence) is { } known && known.RunId == runId) return known.Supported;
        if (round.IsUnknown(connection, runId)) return false;
        try
        {
            using var reply = await client.SendOnPinnedConnectionAsync("COMMAND INFO", connection,
                new Cmd1(RespireCommands.Server.COMMAND_INFO.Verb, "CLUSTERSCAN"), cancellationToken).ConfigureAwait(false);
            var supported = ReadSupport(in reply);
            if (supported is { } value)
            {
                Volatile.Write(ref capability.Evidence, new(runId, value));
                return value;
            }
        }
        catch (RespireServerException error) when (ClusterScanCommandErrors.IsMetadataUnavailable(error))
        {
            // No definitive capability evidence was returned.
        }
        round.RecordUnknown(connection, runId);
        return false;
    }

    internal static bool? ReadSupport(in RespValue reply)
    {
        if (reply.Type != RespDataType.Array || reply.AsArray().Length != 1) return null;
        var entry = reply.AsArray()[0];
        if (entry.IsNull) return false;
        if (entry.Type == RespDataType.Array && entry.AsArray().Length > 0
            && entry.AsArray()[0].Type is RespDataType.BulkString or RespDataType.SimpleString
            && ClusterInspectionParser.Text(in entry.AsArray()[0]).Equals("clusterscan", StringComparison.OrdinalIgnoreCase))
            return true;
        return null;
    }
}

internal static class ClusterScanCommandErrors
{
    internal static bool IsMetadataUnavailable(RespireServerException error)
        => IsDenied(error) || IsUnknown(error, "COMMAND");
    internal static bool IsDenied(RespireServerException error) => error.Code == "NOPERM";

    internal static bool IsUnknown(RespireServerException error, string command)
    {
        const string prefix = "ERR unknown command ";
        if (error.Code != "ERR" || !error.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var name = error.Message.AsSpan(prefix.Length).TrimStart();
        if (name.IsEmpty) return false;
        if (name[0] is '\'' or '"')
        {
            var end = name[1..].IndexOf(name[0]);
            return end >= 0 && name.Slice(1, end).Equals(command, StringComparison.OrdinalIgnoreCase);
        }
        var separator = name.IndexOfAny(' ', ',');
        return (separator < 0 ? name : name[..separator]).Equals(command, StringComparison.OrdinalIgnoreCase);
    }
}
