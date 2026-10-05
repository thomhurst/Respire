using Testcontainers.Redis;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.Testing;

/// <summary>
/// Session-wide Redis container for integration tests. Each test gets its own logical database,
/// numbered by TUnit's per-test isolation ID, so suites with more tests than
/// <see cref="DatabaseCount"/> must split or share a database explicitly.
/// </summary>
/// <remarks>
/// Database 0 is never assigned to a test (isolation IDs start at 1). It is reserved as
/// <see cref="ScratchDatabase"/> for tests that need a second database; they must use unique keys there.
/// Use <c>SharedType.Keyed</c> to get a separate container for tests that change server-wide state.
/// </remarks>
public class RedisTestContainer : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The number of logical databases the container is started with.</summary>
    public const int DatabaseCount = 4096;

    /// <summary>A database that no test owns. Keys written there must be unique to the writing test.</summary>
    public const int ScratchDatabase = 0;

    private const ushort RedisPort = 6379;

    private readonly string[] _serverArguments;
    private RedisContainer? _container;

    public RedisTestContainer() : this([])
    {
    }

    /// <summary>Adds redis-server arguments, such as an eviction policy, for a dedicated keyed container.</summary>
    protected RedisTestContainer(params string[] serverArguments) => _serverArguments = serverArguments;

    private RedisContainer Container =>
        _container ?? throw new InvalidOperationException("Redis container has not been initialized.");

    public int Database
    {
        get
        {
            var database = TestContext.Current?.Isolation.UniqueId
                ?? throw new InvalidOperationException("A Redis database can only be assigned within a test context.");

            if (database is <= ScratchDatabase or >= DatabaseCount)
            {
                throw new InvalidOperationException(
                    $"Test isolation ID {database} is outside the Redis database range 1..{DatabaseCount - 1}. "
                    + $"TUnit numbers every test it builds, so this test project now builds more tests than "
                    + $"RedisTestContainer has databases. Raise {nameof(DatabaseCount)} or move tests to another project.");
            }

            return database;
        }
    }

    // CI runs the shared typed scenarios with each protocol; explicit per-test choices still override it.
    public string ConnectionString => $"redis://{Host}:{Port}/{Database}?protocol={TestProtocol}";
    private static string TestProtocol => Environment.GetEnvironmentVariable("RESPIRE_TEST_PROTOCOL") switch
    {
        null or "" => "auto",
        "2" => "2",
        "3" => "3",
        var value => throw new InvalidOperationException($"Invalid RESPIRE_TEST_PROTOCOL '{value}'; expected 2 or 3."),
    };
    public string StackExchangeConnectionString => $"{Host}:{Port},defaultDatabase={Database},allowAdmin=true";
    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(RedisPort);

    public async Task InitializeAsync()
    {
        var image = Environment.GetEnvironmentVariable("RESPIRE_TEST_REDIS_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
        {
            image = "redis:7.0.15";
        }
        var container = new RedisBuilder(image)
            .WithCommand(["redis-server", "--databases", DatabaseCount.ToString(), .. _serverArguments])
            .Build();
        _container = container;

        try
        {
            await container.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await ResetContainerAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetContainerAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask ResetContainerAsync()
    {
        var container = _container;
        _container = null;

        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
