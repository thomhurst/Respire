using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Respire.Protocol;

/// <summary>
/// Static methods for executing zero-argument Redis commands with pre-compiled RESP protocol
/// Provides maximum performance with zero allocations for common commands
/// </summary>
public static class RespCommandMethods
{
    /// <summary>
    /// Sends PING command. Returns PONG if server is responsive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendPing(Socket socket) => socket.Send(RespCommands.Ping);
    
    /// <summary>
    /// Sends QUIT command to gracefully close connection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendQuit(Socket socket) => socket.Send(RespCommands.Quit);
    
    /// <summary>
    /// Sends RANDOMKEY command. Returns a random key from the database.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendRandomKey(Socket socket) => socket.Send(RespCommands.RandomKey);
    
    /// <summary>
    /// Sends DBSIZE command. Returns the number of keys in the current database.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendDbSize(Socket socket) => socket.Send(RespCommands.DbSize);
    
    /// <summary>
    /// Sends INFO command. Returns server information and statistics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendInfo(Socket socket) => socket.Send(RespCommands.Info);
    
    /// <summary>
    /// Sends INFO SERVER command. Returns server-specific information.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendInfoServer(Socket socket) => socket.Send(RespCommands.InfoServer);
    
    /// <summary>
    /// Sends INFO CLIENTS command. Returns client connection information.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendInfoClients(Socket socket) => socket.Send(RespCommands.InfoClients);
    
    /// <summary>
    /// Sends INFO MEMORY command. Returns memory usage information.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendInfoMemory(Socket socket) => socket.Send(RespCommands.InfoMemory);
    
    /// <summary>
    /// Sends INFO STATS command. Returns general statistics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendInfoStats(Socket socket) => socket.Send(RespCommands.InfoStats);
    
    /// <summary>
    /// Sends INFO REPLICATION command. Returns replication information.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendInfoReplication(Socket socket) => socket.Send(RespCommands.InfoReplication);
    
    /// <summary>
    /// Sends INFO KEYSPACE command. Returns keyspace information.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendInfoKeyspace(Socket socket) => socket.Send(RespCommands.InfoKeyspace);
    
    /// <summary>
    /// Sends TIME command. Returns server time as Unix timestamp and microseconds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendTime(Socket socket) => socket.Send(RespCommands.Time);
    
    /// <summary>
    /// Sends FLUSHDB command. Removes all keys from the current database.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendFlushDb(Socket socket) => socket.Send(RespCommands.FlushDb);
    
    /// <summary>
    /// Sends FLUSHALL command. Removes all keys from all databases.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendFlushAll(Socket socket) => socket.Send(RespCommands.FlushAll);
    
    /// <summary>
    /// Sends SAVE command. Synchronously saves database to disk.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendSave(Socket socket) => socket.Send(RespCommands.Save);
    
    /// <summary>
    /// Sends BGSAVE command. Asynchronously saves database to disk.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendBgSave(Socket socket) => socket.Send(RespCommands.BgSave);
    
    /// <summary>
    /// Sends LASTSAVE command. Returns Unix timestamp of last successful save.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendLastSave(Socket socket) => socket.Send(RespCommands.LastSave);
    
    /// <summary>
    /// Sends MULTI command. Starts a transaction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendMulti(Socket socket) => socket.Send(RespCommands.Multi);
    
    /// <summary>
    /// Sends EXEC command. Executes queued commands in a transaction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendExec(Socket socket) => socket.Send(RespCommands.Exec);
    
    /// <summary>
    /// Sends DISCARD command. Discards queued commands in a transaction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendDiscard(Socket socket) => socket.Send(RespCommands.Discard);
    
    /// <summary>
    /// Sends ROLE command. Returns the role of the Redis instance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendRole(Socket socket) => socket.Send(RespCommands.Role);
    
    /// <summary>
    /// Sends COMMAND HELP command. Returns help for Redis commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendCommandHelp(Socket socket) => socket.Send(RespCommands.CommandHelp);
    
    /// <summary>
    /// Sends COMMAND LIST command. Returns list of all command names.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendCommandList(Socket socket) => socket.Send(RespCommands.CommandList);
    
    /// <summary>
    /// Sends COMMAND COUNT command. Returns total number of commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendCommandCount(Socket socket) => socket.Send(RespCommands.CommandCount);
    
    /// <summary>
    /// Sends COMMAND DOCS command. Returns documentation for all commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendCommandDocs(Socket socket) => socket.Send(RespCommands.CommandDocs);
    
    /// <summary>
    /// Sends CLIENT HELP command. Returns help for client commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendClientHelp(Socket socket) => socket.Send(RespCommands.ClientHelp);
    
    /// <summary>
    /// Sends CLIENT LIST command. Returns information about client connections.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendClientList(Socket socket) => socket.Send(RespCommands.ClientList);
    
    /// <summary>
    /// Sends CONFIG HELP command. Returns help for configuration commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendConfigHelp(Socket socket) => socket.Send(RespCommands.ConfigHelp);
    
    /// <summary>
    /// Sends FUNCTION HELP command. Returns help for function commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendFunctionHelp(Socket socket) => socket.Send(RespCommands.FunctionHelp);
    
    /// <summary>
    /// Sends FUNCTION LIST command. Returns list of loaded functions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendFunctionList(Socket socket) => socket.Send(RespCommands.FunctionList);
    
    /// <summary>
    /// Sends FUNCTION DUMP command. Returns serialized payload of all loaded libraries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendFunctionDump(Socket socket) => socket.Send(RespCommands.FunctionDump);
    
    /// <summary>
    /// Sends FUNCTION FLUSH command. Deletes all loaded libraries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendFunctionFlush(Socket socket) => socket.Send(RespCommands.FunctionFlush);
    
    /// <summary>
    /// Sends MEMORY HELP command. Returns help for memory commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendMemoryHelp(Socket socket) => socket.Send(RespCommands.MemoryHelp);
    
    /// <summary>
    /// Sends MODULE HELP command. Returns help for module commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendModuleHelp(Socket socket) => socket.Send(RespCommands.ModuleHelp);
    
    /// <summary>
    /// Sends MODULE LIST command. Returns list of loaded modules.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendModuleList(Socket socket) => socket.Send(RespCommands.ModuleList);
    
    /// <summary>
    /// Sends SCRIPT HELP command. Returns help for script commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendScriptHelp(Socket socket) => socket.Send(RespCommands.ScriptHelp);
    
    /// <summary>
    /// Sends SCRIPT FLUSH command. Removes all scripts from the script cache.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendScriptFlush(Socket socket) => socket.Send(RespCommands.ScriptFlush);
    
    /// <summary>
    /// Sends SCRIPT KILL command. Kills currently executing script.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendScriptKill(Socket socket) => socket.Send(RespCommands.ScriptKill);
    
    /// <summary>
    /// Sends XINFO HELP command. Returns help for stream introspection commands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendXInfoHelp(Socket socket) => socket.Send(RespCommands.XInfoHelp);
    
    /// <summary>
    /// Sends ACL LIST command. Returns list of ACL rules for all users.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendAclList(Socket socket) => socket.Send(RespCommands.AclList);
}