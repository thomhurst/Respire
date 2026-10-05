using System.Collections.ObjectModel;
using Respire.Protocol;

namespace Respire.Search;

public sealed partial class RespireSearchClient
{
    /// <summary>Reads Search configuration from one selected node. The default option is <c>*</c>.</summary>
    /// <remarks>
    /// Results own their names and values; null values are preserved. Option names and wildcard support
    /// are server-defined. FT.CONFIG is deprecated since Redis 8.0; prefer
    /// <c>client.Server.ConfigAsync("search-*")</c> on modern servers. This command never fans out,
    /// rejects key-prefixed views, and preserves the local client-side cache. Server ACLs still apply.
    /// Use a separate standalone client connected to the intended node for explicit node selection.
    /// </remarks>
    public async ValueTask<IReadOnlyDictionary<string, string?>> GetConfigurationAsync(
        string option = "*", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(option);
        using var result = await _commands.ConfigGetAsync(option, cancellationToken).ConfigureAwait(false);
        const string command = "FT.CONFIG GET";
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (result.Type == RespDataType.Map)
        {
            RespireSearchReply.RequirePairs(result, command);
            for (var i = 0; i < result.Count; i += 2) AddConfigurationPair(result[i], result[i + 1], values);
        }
        else if (result.Type == RespDataType.Array)
        {
            for (var i = 0; i < result.Count; i++)
            {
                var pair = result[i];
                if (pair.Type != RespDataType.Array || pair.Count != 2)
                    throw RespireSearchReply.Unexpected(command, "an entry that is not a two-element array");
                AddConfigurationPair(pair[0], pair[1], values);
            }
        }
        else
        {
            throw RespireSearchReply.Unexpected(command, "a value that is not an array or map");
        }

        return new ReadOnlyDictionary<string, string?>(values);
    }

    /// <summary>Sets one Search configuration option on one selected node.</summary>
    /// <remarks>
    /// Requires <see cref="RespireOptions.AllowAdmin"/> on RespireClient; server ACLs still apply.
    /// FT.CONFIG is deprecated since Redis 8.0; prefer
    /// <c>client.Server.SetConfigAsync("search-timeout", "1000")</c> on modern servers.
    /// Changes are not persisted across server restarts, and some options cannot change at runtime.
    /// This command never fans out, rejects key-prefixed views, and conservatively invalidates the local cache.
    /// Use a separate standalone client connected to the intended node for explicit node selection.
    /// Cancellation abandons the wait and does not undo a change already accepted by the server.
    /// </remarks>
    public async ValueTask SetConfigurationAsync(string option, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(option);
        ArgumentNullException.ThrowIfNull(value);
        using var result = await _commands.ConfigSetAsync(option, value, cancellationToken).ConfigureAwait(false);
        if (result.Type != RespDataType.SimpleString || result.AsString() != "OK")
            throw RespireSearchReply.Unexpected("FT.CONFIG SET", "a value other than the simple string OK");
    }

    private static void AddConfigurationPair(
        RespireResult name, RespireResult value, Dictionary<string, string?> values)
    {
        const string command = "FT.CONFIG GET";
        if (name.Type is not (RespDataType.BulkString or RespDataType.SimpleString) || name.IsNull)
            throw RespireSearchReply.Unexpected(command, "a non-string option name");
        var option = name.AsString();
        if (option.Length == 0)
            throw RespireSearchReply.Unexpected(command, "an empty option name");
        if (!value.IsNull && value.Type is not (RespDataType.BulkString or RespDataType.SimpleString))
            throw RespireSearchReply.Unexpected(command, "a non-string option value");
        if (!values.TryAdd(option, value.IsNull ? null : value.AsString()))
            throw RespireSearchReply.Unexpected(command, "a duplicate option name");
    }
}
