using System.Globalization;
using System.Text;
using Respire.Protocol;
using Respire.TestSupport;

namespace Respire.IntegrationTests;

/// <summary>
/// Sends fixture setup and control commands over one direct RESP2 connection per published node port,
/// instead of starting a <c>redis-cli</c> process through <c>docker exec</c> for every command.
/// Replies are rendered as <c>redis-cli -e --raw</c> prints them, and error replies throw.
/// </summary>
internal sealed class NodeCommandSessions(string host) : IAsyncDisposable
{
    private readonly Dictionary<int, TestRespSession> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<string> CommandAsync(int port, string[] arguments, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.TryGetValue(port, out var session))
            {
                session = await TestRespSession.ConnectAsync(new RespireOptions
                {
                    Endpoints = [new(host, port)], Protocol = RespireProtocol.Resp2,
                }, cancellationToken);
                _sessions.Add(port, session);
            }
            RespValue reply;
            try { reply = await session.CommandAsync(arguments).WaitAsync(cancellationToken); }
            catch
            {
                // A cancelled or failed exchange can leave a partial reply on the socket.
                _sessions.Remove(port);
                await session.DisposeAsync();
                throw;
            }
            using (reply)
            {
                if (reply.IsError)
                    throw new InvalidOperationException($"Node {host}:{port} rejected {string.Join(' ', arguments)}: {reply.GetErrorMessage()}");
                var text = new StringBuilder();
                Render(reply, text);
                return text.ToString();
            }
        }
        finally { _gate.Release(); }
    }

    private static void Render(RespValue value, StringBuilder text)
    {
        switch (value.Type)
        {
            // The sessions speak RESP2, so every aggregate is an array; nested items print one per line.
            case RespireDataType.Array:
                foreach (var item in value.AsArray()) Render(item, text);
                return;
            case RespireDataType.Integer:
                text.Append(value.AsInteger().ToString(CultureInfo.InvariantCulture)).Append('\n');
                return;
            default:
                text.Append(value.AsString()).Append('\n');
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values) await session.DisposeAsync();
        _sessions.Clear();
        _gate.Dispose();
    }
}
