using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.Testing;

/// <summary>
/// Session-wide Redis container for integration tests. Tests share <see cref="Database"/> and are isolated by
/// <see cref="KeyPrefix"/>, which <see cref="ConnectionString"/> applies to every Respire client built from it.
/// </summary>
/// <remarks>
/// Clients built by hand, and other client libraries, must apply <see cref="KeyPrefix"/> themselves.
/// Pub/sub channels are server-wide and are not prefixed; tests must still use unique channel names.
/// A test that inspects or clears a whole database (SCAN, KEYS, DBSIZE, FLUSHDB, SWAPDB, ...) uses
/// <see cref="IsolatedDatabase"/> instead: a database leased for the test, emptied and returned when it ends.
/// <see cref="ScratchDatabase"/> serves tests that need a second database; their keys there must be unique.
/// Use <c>SharedType.Keyed</c> to get a separate container for tests that change server-wide state.
/// Set <c>RESPIRE_TEST_REDIS_IMAGE</c> to a Valkey or Microsoft Garnet image to run the suite against that server.
/// </remarks>
public class RedisTestContainer : IAsyncInitializer, IAsyncDisposable, ITestEndEventReceiver
{
    /// <summary>The number of logical databases the container is started with; Garnet allows at most 256.</summary>
    public const int DatabaseCount = 256;

    /// <summary>The database every prefixed test shares.</summary>
    public const int SharedDatabase = 0;

    /// <summary>A second database that no test owns. Keys written there must be unique to the writing test.</summary>
    public const int ScratchDatabase = 1;

    /// <summary>The default Redis image, and the reference server for tests that pin Redis semantics.</summary>
    public const string ReferenceRedisImage = "redis:7.0.15";

    private const int FirstIsolatedDatabase = 2;
    private const ushort RedisPort = 6379;

    private readonly string[] _serverArguments;
    // Lazy values lease exactly once even when GetOrAdd races and discards a losing value.
    private readonly ConcurrentDictionary<int, Lazy<int>> _leasedDatabases = new();
    private readonly ConcurrentBag<int> _freeDatabases = [.. Enumerable.Range(FirstIsolatedDatabase, DatabaseCount - FirstIsolatedDatabase)];
    private readonly SemaphoreSlim _freeDatabaseCount = new(DatabaseCount - FirstIsolatedDatabase);
    private IContainer? _container;

    public RedisTestContainer() : this([])
    {
    }

    /// <summary>Adds redis-server arguments, such as an eviction policy, for a dedicated keyed container.</summary>
    protected RedisTestContainer(params string[] serverArguments) => _serverArguments = serverArguments;

    /// <summary>Selects the server image. Version-specific tests can pin their reference server.</summary>
    protected virtual string ServerImage
    {
        get
        {
            var image = ConfiguredImage;
            // redis-server arguments do not apply to Garnet, so such containers keep the reference Redis server.
            return string.IsNullOrWhiteSpace(image) || (IsGarnetImage(image) && _serverArguments.Length != 0)
                ? ReferenceRedisImage
                : image;
        }
    }

    /// <summary>Whether this container runs Microsoft Garnet.</summary>
    public bool IsGarnet => IsGarnetImage(ServerImage);

    /// <summary>Whether <c>RESPIRE_TEST_REDIS_IMAGE</c> selects Garnet for the shared container.</summary>
    public static bool GarnetSelected => IsGarnetImage(ConfiguredImage ?? "");

    private static string? ConfiguredImage => Environment.GetEnvironmentVariable("RESPIRE_TEST_REDIS_IMAGE");

    private static bool IsGarnetImage(string image) => image.Contains("microsoft/garnet", StringComparison.OrdinalIgnoreCase);

    private IContainer Container =>
        _container ?? throw new InvalidOperationException("Redis container has not been initialized.");

    /// <summary>The database prefixed tests share; isolation comes from <see cref="KeyPrefix"/>.</summary>
    public int Database => SharedDatabase;

    /// <summary>The key prefix that isolates the current test, numbered by its TUnit isolation ID.</summary>
    public string KeyPrefix => $"test:{TestId}:";

    /// <summary>
    /// A database leased to the current test alone, for tests that inspect or clear a whole database.
    /// It is emptied and returned to the pool when the test ends.
    /// </summary>
    public int IsolatedDatabase =>
        _leasedDatabases.GetOrAdd(TestId, static (_, container) => new Lazy<int>(container.LeaseDatabase), this).Value;

    /// <summary>A connection string for <see cref="IsolatedDatabase"/>, without a key prefix.</summary>
    public string IsolatedConnectionString => $"redis://{Host}:{Port}/{IsolatedDatabase}?protocol={TestProtocol}";

    private static int TestId => TestContext.Current?.Isolation.UniqueId
        ?? throw new InvalidOperationException("Test isolation is only available within a test context.");

    // CI runs the shared typed scenarios with each protocol; explicit per-test choices still override it.
    public string ConnectionString =>
        $"redis://{Host}:{Port}/{SharedDatabase}?protocol={TestProtocol}&keyPrefix={Uri.EscapeDataString(KeyPrefix)}";
    private static string TestProtocol => Environment.GetEnvironmentVariable("RESPIRE_TEST_PROTOCOL") switch
    {
        null or "" => "auto",
        "2" => "2",
        "3" => "3",
        var value => throw new InvalidOperationException($"Invalid RESPIRE_TEST_PROTOCOL '{value}'; expected 2 or 3."),
    };

    /// <summary>A StackExchange.Redis configuration for <see cref="Database"/>; apply <see cref="KeyPrefix"/> with <c>WithKeyPrefix</c>.</summary>
    public string StackExchangeConnectionString => $"{Host}:{Port},defaultDatabase={SharedDatabase},allowAdmin=true";

    /// <summary>A StackExchange.Redis configuration for <see cref="IsolatedDatabase"/>.</summary>
    public string IsolatedStackExchangeConnectionString => $"{Host}:{Port},defaultDatabase={IsolatedDatabase},allowAdmin=true";
    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(RedisPort);

    public async Task InitializeAsync()
    {
        var image = ServerImage;
        var databases = DatabaseCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // A failed start is removed by the retry helper, which also replaces it after a host-port bind collision.
        _container = IsGarnet
            ? await ContainerStartRetry.StartAsync(() => new ContainerBuilder(image)
                .WithCommand("--max-databases", databases, "--lua",
                    "--memory", "256m", "--page", "4m", "--segment", "16m", "--index", "8m")
                .WithPortBinding(RedisPort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RedisPort))
                .Build(), [RedisPort]).ConfigureAwait(false)
            : await ContainerStartRetry.StartAsync(() => new RedisBuilder(image)
                .WithCommand(["redis-server", "--databases", databases, .. _serverArguments])
                .Build(), [RedisPort]).ConfigureAwait(false);
    }

    private int LeaseDatabase()
    {
        // Few tests need a whole database, so this waits only if many run at once or leases leak.
        if (!_freeDatabaseCount.Wait(TimeSpan.FromMinutes(2)) || !_freeDatabases.TryTake(out var database))
        {
            throw new InvalidOperationException($"No isolated database became free; {_leasedDatabases.Count} are leased.");
        }

        return database;
    }

    async ValueTask ITestEndEventReceiver.OnTestEnd(TestContext context)
    {
        if (!_leasedDatabases.TryRemove(context.Isolation.UniqueId, out var lease) || !lease.IsValueCreated)
        {
            return;
        }

        var database = lease.Value;
        if (_container is not null)
        {
            // A database that could not be emptied stays out of the pool, so no later test inherits its keys.
            await DeleteAllKeysAsync(database).ConfigureAwait(false);
        }

        _freeDatabases.Add(database);
        _freeDatabaseCount.Release();
    }

    // FLUSHDB would send a flush invalidation to every tracking client on the server, emptying the
    // client-side caches of concurrent tests. Deleting the leased database's keys invalidates only those keys.
    private async Task DeleteAllKeysAsync(int database)
    {
        await using var client = await RespireClient.ConnectAsync($"redis://{Host}:{Port}/{database}").ConfigureAwait(false);
        var cursor = "0";
        do
        {
            using var page = await client.ExecuteAsync("SCAN", cursor, "COUNT", 1000).ConfigureAwait(false);
            cursor = page[0].AsString()!;
            var keys = page[1];
            if (keys.Count == 0) continue;
            var arguments = new RespireValue[keys.Count];
            for (var index = 0; index < keys.Count; index++) arguments[index] = keys[index].AsBytes();
            using var deleted = await client.ExecuteAsync("UNLINK", arguments).ConfigureAwait(false);
        }
        while (cursor != "0");
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

/// <summary>
/// Skips a test or class when <c>RESPIRE_TEST_REDIS_IMAGE</c> selects Garnet, before its fixture starts.
/// The reason names the Garnet version and the missing command, so the report shows each known gap.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipOnGarnetAttribute(string reason) : SkipAttribute(reason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(RedisTestContainer.GarnetSelected);
}
