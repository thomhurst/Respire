namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    // Arity includes the command name. Option syntax remains the handler's responsibility.
    private sealed record Command(int MinimumArity, int MaximumArity,
        Func<RespireFakeServer, Connection, byte[][], FakeReply> Execute);

    private static readonly Dictionary<string, Command> Commands = new(StringComparer.Ordinal)
    {
        ["HELLO"] = new(1, int.MaxValue, static (_, connection, args) => Hello(connection, args)),
        ["PING"] = new(1, 2, static (_, _, args) => args.Length == 1 ? FakeReply.Simple("PONG") : FakeReply.Bulk(args[1])),
        ["ECHO"] = new(2, 2, static (_, _, args) => FakeReply.Bulk(args[1])),
        ["SELECT"] = new(2, 2, static (_, _, args) => Token(args[1]) == "0" ? FakeReply.Ok : Syntax("SELECT")),
        ["CLIENT"] = new(2, int.MaxValue, static (_, connection, args) => Client(connection, args)),
        ["GET"] = new(2, 2, static (server, _, args) => FakeReply.Bulk(server.Find(args[1])?.Value)),
        ["SET"] = new(3, int.MaxValue, static (server, _, args) => server.Set(args)),
        ["MGET"] = new(2, int.MaxValue, static (server, _, args) => FakeReply.Array(args.Skip(1).Select(key => FakeReply.Bulk(server.Find(key)?.Value)).ToArray())),
        ["MSET"] = new(3, int.MaxValue, static (server, _, args) => server.MultiSet("MSET", args)),
        ["MSETNX"] = new(3, int.MaxValue, static (server, _, args) => server.MultiSet("MSETNX", args)),
        ["DEL"] = new(2, int.MaxValue, static (server, _, args) => FakeReply.Integer(args.Skip(1).Count(server.Remove))),
        ["UNLINK"] = new(2, int.MaxValue, static (server, _, args) => FakeReply.Integer(args.Skip(1).Count(server.Remove))),
        ["EXISTS"] = new(2, int.MaxValue, static (server, _, args) => FakeReply.Integer(args.Skip(1).Count(key => server.Find(key) is not null))),
        ["TYPE"] = new(2, 2, static (server, _, args) => FakeReply.Simple(server.Find(args[1]) is null ? "none" : "string")),
        ["GETDEL"] = new(2, 2, static (server, _, args) => server.GetDelete(args[1])),
        ["GETSET"] = new(3, 3, static (server, _, args) => server.GetSet(args[1], args[2])),
        ["STRLEN"] = new(2, 2, static (server, _, args) => FakeReply.Integer(server.Find(args[1])?.Value.Length ?? 0)),
        ["APPEND"] = new(3, 3, static (server, _, args) => server.Append(args[1], args[2])),
        ["INCR"] = new(2, 2, static (server, _, args) => server.Increment(args[1], 1)),
        ["DECR"] = new(2, 2, static (server, _, args) => server.Increment(args[1], -1)),
        ["INCRBY"] = new(3, 3, static (server, _, args) => server.Increment(args[1], Integer(args[2]))),
        ["DECRBY"] = new(3, 3, static (server, _, args) => server.Increment(args[1], Integer(args[2]), subtract: true)),
        ["GETEX"] = new(2, int.MaxValue, static (server, _, args) => server.GetExpire(args)),
        ["EXPIRE"] = new(3, int.MaxValue, static (server, _, args) => server.Expire("EXPIRE", args)),
        ["PEXPIRE"] = new(3, int.MaxValue, static (server, _, args) => server.Expire("PEXPIRE", args)),
        ["EXPIREAT"] = new(3, int.MaxValue, static (server, _, args) => server.Expire("EXPIREAT", args)),
        ["PEXPIREAT"] = new(3, int.MaxValue, static (server, _, args) => server.Expire("PEXPIREAT", args)),
        ["TTL"] = new(2, 2, static (server, _, args) => server.TimeToLive("TTL", args[1])),
        ["PTTL"] = new(2, 2, static (server, _, args) => server.TimeToLive("PTTL", args[1])),
        ["EXPIRETIME"] = new(2, 2, static (server, _, args) => server.TimeToLive("EXPIRETIME", args[1])),
        ["PEXPIRETIME"] = new(2, 2, static (server, _, args) => server.TimeToLive("PEXPIRETIME", args[1])),
        ["PERSIST"] = new(2, 2, static (server, _, args) => server.Persist(args[1])),
    };
}
