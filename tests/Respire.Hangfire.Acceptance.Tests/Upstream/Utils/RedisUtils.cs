using Respire;
using Respire.StackExchangeCompat;
using StackExchange.Redis;

namespace Hangfire.Redis.Tests.Utils;

// Replaces only the upstream test fixture, never a production storage behavior.
public static class RedisUtils
{
    private static RespireConnectionMultiplexer? _connection;
    private static ConnectionMultiplexer? _control;
    private static RedisTestContainer? _fixture;

    public static IConnectionMultiplexer Connection =>
        _connection ?? throw new InvalidOperationException("The upstream test fixture is not initialized.");

    public static IDatabase ShimDatabase => Connection.GetDatabase(GetDb());

    public static void Initialize(RedisTestContainer fixture)
    {
        if (_connection is not null) throw new InvalidOperationException("Upstream tests must run sequentially.");
        var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Connections = 1,
        });
        try
        {
            _control = ConnectionMultiplexer.Connect(fixture.StackExchangeConnectionString);
            _fixture = fixture;
            _connection = connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static async ValueTask DisposeAsync()
    {
        var connection = _connection;
        var control = _control;
        _connection = null;
        _control = null;
        _fixture = null;
        try
        {
            if (connection is not null) await connection.DisposeAsync();
        }
        finally
        {
            control?.Dispose();
        }
    }

    public static IServer GetFirstServer() => Connection.GetServer(Connection.GetEndPoints()[0]);
    // Upstream setup and assertions use an independent client. Production
    // constructors always receive Connection, ShimDatabase or CreateSubscriber.
    public static IDatabase CreateClient() =>
        (_control ?? throw new InvalidOperationException("The control client is not initialized.")).GetDatabase(GetDb());
    public static ISubscriber CreateSubscriber() => Connection.GetSubscriber();
    public static string GetHostAndPort() => $"{GetHost()}:{GetPort()}";
    public static string GetHost() => Fixture.Host;
    public static int GetPort() => Fixture.Port;
    public static int GetDb() => Fixture.Database;

    private static RedisTestContainer Fixture =>
        _fixture ?? throw new InvalidOperationException("The upstream test fixture is not initialized.");
}
