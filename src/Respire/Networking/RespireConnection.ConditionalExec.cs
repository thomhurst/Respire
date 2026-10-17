using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    // Capability belongs to the physical socket, so replacements and failovers probe again.
    private int _conditionalExecSupport;

    internal async ValueTask EnsureConditionalExecSupportedAsync(CancellationToken cancellationToken, CommandDeadline deadline)
    {
        var support = Volatile.Read(ref _conditionalExecSupport);
        if (support == 0)
        {
            using var reply = await SendCoreAsync(new Cmd1(Verbs.Info, "SERVER"),
                discardRepliesBefore: 0, throwOnError: false, cancellationToken,
                commandName: "INFO SERVER", commandDeadline: deadline, pinToConnection: true).ConfigureAwait(false);
            if (reply.IsError)
                throw new NotSupportedException("Conditional EXEC requires Valkey 9.2+ and permission to read INFO SERVER.",
                    ResponseReader.ServerError(in reply, "INFO SERVER", new(Host, Port)));

            string? server = null, version = null;
            if (reply.Type is RespireDataType.BulkString or RespireDataType.SimpleString or RespireDataType.VerbatimString)
            {
                foreach (var line in reply.AsString().Split('\n'))
                {
                    if (line.StartsWith("server_name:", StringComparison.Ordinal)) server = line[12..].Trim();
                    else if (line.StartsWith("valkey_version:", StringComparison.Ordinal)) version = line[15..].Trim();
                }
            }
            var versionText = version?.Split('-')[0];
            support = string.Equals(server, "valkey", StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(versionText, out var parsed) && parsed >= new Version(9, 2) ? 1 : -1;
            Volatile.Write(ref _conditionalExecSupport, support);
        }
        if (support < 0)
            throw new NotSupportedException("Conditional EXEC requires Valkey 9.2+; Redis and older Valkey servers are not supported.");
    }
}
