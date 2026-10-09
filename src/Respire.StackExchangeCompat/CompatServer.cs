using System.Net;
using StackExchange.Redis;

#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal sealed partial class CompatServer(RespireConnectionMultiplexer owner, RespireClient client, EndPoint endpoint) : IServer
{
    internal RespireClient Client => client;
    public IConnectionMultiplexer Multiplexer => owner;
    public EndPoint EndPoint => endpoint;
    public bool IsConnected
    {
        get
        {
            try { owner.Wait(owner.Run(async token => { await client.Core.EnsureConnectedAsync(token).ConfigureAwait(false); return true; })); }
            catch (RespireConnectionException) { return false; }
            return client.IsConnected;
        }
    }
    public bool IsReplica => owner.Wait(owner.Run(async token =>
        (await client.Server.RoleAsync(token).ConfigureAwait(false)).Kind == RespireServerRoleKind.Replica));
    private static void ValidateFlags(CommandFlags flags)
    {
        if (flags != CommandFlags.None) throw Compatibility.Unsupported($"IServer CommandFlags {flags}; use None for endpoint-targeted calls");
    }
    public Task<string?> InfoRawAsync(RedisValue section = default, CommandFlags flags = CommandFlags.None)
    {
        ValidateFlags(flags);
        return owner.Run<string?>(async token => await client.Server.InfoAsync(section.IsNull ? null : (string?)section, token).ConfigureAwait(false));
    }
    public string? InfoRaw(RedisValue section = default, CommandFlags flags = CommandFlags.None) => owner.Wait(InfoRawAsync(section, flags));
    public Task<DateTime> TimeAsync(CommandFlags flags = CommandFlags.None)
    {
        ValidateFlags(flags);
        return owner.Run(async token => (await client.Server.TimeAsync(token).ConfigureAwait(false)).UtcDateTime);
    }
    public DateTime Time(CommandFlags flags = CommandFlags.None) => owner.Wait(TimeAsync(flags));
    public void Wait(Task task) => owner.Wait(task);
    public T Wait<T>(Task<T> task) => owner.Wait(task);
    public void WaitAll(params Task[] tasks) => owner.WaitAll(tasks);
    public bool TryWait(Task task) { try { Wait(task); return true; } catch (TimeoutException) { return false; } }
}
