using System.Text;

namespace Respire.Tests.Networking;

internal sealed class ReplicaRedirectTestFixture : IAsyncDisposable
{
    internal FakeRespServer Target { get; } = new(32)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Bytes("%1\r\n$5\r\nproto\r\n:3\r\n"),
            "CLIENT ID" => Bytes(":99\r\n"),
            _ when command.StartsWith("CLIENT KILL ") => Bytes(":0\r\n"),
            _ when command.StartsWith("EVALSHA ") => Bytes("-NOSCRIPT missing\r\n"),
            _ when command.StartsWith("EVAL ") => Bytes(":1\r\n"),
            _ => FakeRespServer.OkReply,
        },
    };

    internal FakeRespServer Source { get; }
    private FakeRespServer? Sentinel { get; }
    internal RespireOptions Options { get; }

    internal ReplicaRedirectTestFixture(bool sentinel, bool cache, int? targetPort = null)
    {
        Source = new(32)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Bytes("%1\r\n$5\r\nproto\r\n:3\r\n"),
                "ROLE" => Bytes("*3\r\n+master\r\n:0\r\n*0\r\n"),
                "CLIENT ID" => Bytes(":41\r\n"),
                _ when command.StartsWith("CLIENT KILL ") => Bytes(":0\r\n"),
                _ when command.StartsWith("EVAL") => Bytes($"-REDIRECT 127.0.0.1:{targetPort ?? Target.Port}\r\n"),
                _ => FakeRespServer.OkReply,
            },
        };
        if (sentinel)
        {
            Sentinel = new(64)
            {
                ReplyOverride = (_, command) => command switch
                {
                    "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Bytes($"*2\r\n+127.0.0.1\r\n+{Source.Port}\r\n"),
                    "SENTINEL MASTER mymaster" => Bytes($"*8\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{Source.Port}\r\n+config-epoch\r\n+1\r\n+flags\r\n+master\r\n"),
                    "SUBSCRIBE +switch-master +sdown +odown" => Bytes("*3\r\n+subscribe\r\n++switch-master\r\n:1\r\n*3\r\n+subscribe\r\n++sdown\r\n:2\r\n*3\r\n+subscribe\r\n++odown\r\n:3\r\n"),
                    _ => Bytes("*0\r\n"),
                },
            };
        }
        Options = new()
        {
            Protocol = RespireProtocol.Resp2, Connections = 1, EnableReplicaRedirects = true,
            Endpoints = [new("127.0.0.1", Sentinel?.Port ?? Source.Port)],
            SentinelPrimaryName = sentinel ? "mymaster" : null,
            ClientSideCache = cache ? new() : null,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Sentinel is not null) await Sentinel.DisposeAsync();
        await Source.DisposeAsync();
        await Target.DisposeAsync();
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
