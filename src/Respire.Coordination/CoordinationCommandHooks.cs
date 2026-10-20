using Respire.Internal;
using Respire.Commands;
using Respire.Protocol;

namespace Respire.Coordination;

// Owners explicitly call unhooked legs; no ambient state suppresses application reentrancy.
internal static class CoordinationCommandHooks
{
    internal static ValueTask<TResult> RunAsync<TState, TResult>(IRespireClient client,
        string operation, CancellationToken token, TState state, Func<TState, ValueTask<TResult>> execute)
        => client is RespireClient concrete && concrete.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, operation, token, state, execute)
            : execute(state);

    internal static ValueTask RunAsync<TState>(IRespireClient client,
        string operation, CancellationToken token, TState state, Func<TState, ValueTask> execute)
        => client is RespireClient concrete && concrete.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, operation, token, state, execute)
            : execute(state);

    internal static ValueTask<RespireResult> ExecuteAsync(IRespireClient client, RespireScript script,
        RespireKey[] keys, RespireValue[] args, CancellationToken token)
        => client is RespireClient concrete
            ? DispatchResponseSource<RespireResult>.Run((Client: concrete, Script: script, Keys: keys, Args: args, Token: token),
                static (s, observation) => s.Client.ExecuteScriptBorrowedAsync(s.Script, s.Keys, s.Args, s.Token, observation))
            : client.Scripts.ExecuteLeaseAsync(script, keys, args, token);

    internal static async ValueTask TrackAsync(IRespireClient client, RespireKey key, RespireKey? field,
        CancellationToken token)
    {
        if (client is RespireClient concrete)
        {
            await concrete.TrackKeyUnhookedAsync(key, field, token).ConfigureAwait(false);
        }
        else if (field is { } hashField)
            _ = await client.Hashes.GetBytesAsync(key, hashField, token).ConfigureAwait(false);
        else
            _ = await client.Strings.GetStringAsync(key, token).ConfigureAwait(false);
    }

    internal static ValueTask<RespireTtlResult> ExpiryAsync(IRespireClient client, RespireKey key, RespireKey? field,
        CancellationToken token)
    {
        if (client is not RespireClient concrete)
            return field is { } hashField ? client.Hashes.ExpiryAsync(key, hashField, token) : client.Keys.ExpiryAsync(key, token);
        if (field is { } selectedField)
            return concrete.ConvertResponseUnhookedAsync("HPTTL",
                new Cmd1N(RespireCommands.Hash.HPTTL.Verb, concrete.Key(in key), ["FIELDS", 1, selectedField]), token, concrete,
                static (RespireClient _, in RespValue value) =>
                {
                    if (value.Type != RespireDataType.Array)
                        throw new RespireProtocolException("A single hash field TTL reply must be an array.");
                    var elements = value.AsArray();
                    if (elements.Length != 1 || elements[0].Type != RespireDataType.Integer)
                        throw new RespireProtocolException("A single hash field TTL reply must contain exactly one integer.");
                    return RespireTtlResult.FromRedisMilliseconds(elements[0].AsInteger());
                });
        return concrete.ConvertResponseUnhookedAsync("PTTL", new Cmd1(Verbs.Pttl, concrete.Key(in key)), token, concrete,
            static (RespireClient _, in RespValue value) => RespireTtlResult.FromRedisMilliseconds(ResponseReader.Integer(in value)));
    }
}
