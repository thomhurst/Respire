using System.Diagnostics;
using System.Text;
using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DistributedLockTests(RedisTestContainer fixture)
{
    private const string Key = "locks:job:42";

    private readonly RedisTestContainer _fixture = fixture;
    private RespireClient? _respireClient;
    private ConnectionMultiplexer? _stackExchangeMultiplexer;
    private IDatabase? _stackExchangeDb;

    private RespireClient Client => _respireClient!;
    private IDatabase Db => _stackExchangeDb!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _respireClient = await RespireClient.ConnectAsync(_fixture.ConnectionString);

        _stackExchangeMultiplexer = await ConnectionMultiplexer.ConnectAsync(_fixture.StackExchangeConnectionString);
        _stackExchangeDb = _stackExchangeMultiplexer.GetDatabase();

        await _stackExchangeDb.ExecuteAsync("FLUSHDB");
    }

    [After(Test)]
    public async ValueTask DisposeAsync()
    {
        if (_respireClient is not null)
        {
            await _respireClient.DisposeAsync();
        }

        _stackExchangeMultiplexer?.Dispose();
    }

    [Test]
    public async Task Acquire_WhenFree_StoresGeneratedTokenUnderTheKey()
    {
        await using var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));

        await Assert.That(mutex).IsNotNull();
        await Assert.That(mutex!.Key.ToString()).IsEqualTo(Key);
        await Assert.That(mutex.Expiry).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(mutex.Token.Length).IsEqualTo(32);

        var stored = (byte[]?)await Db.StringGetAsync(Key);
        await Assert.That(stored!.AsSpan().SequenceEqual(mutex.Token.Span)).IsTrue();
    }

    [Test]
    public async Task Acquire_WhenAlreadyHeld_ReturnsNull()
    {
        await using var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));
        await Assert.That(mutex).IsNotNull();

        var contender = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));

        await Assert.That(contender).IsNull();
    }

    [Test]
    public async Task Release_FreesTheKeyForTheNextOwner()
    {
        var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));

        await Assert.That(await mutex!.ReleaseAsync()).IsTrue();
        await Assert.That(await Db.KeyExistsAsync(Key)).IsFalse();

        await using var next = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));
        await Assert.That(next).IsNotNull();
    }

    [Test]
    public async Task Release_IsIdempotent()
    {
        var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));

        await Assert.That(await mutex!.ReleaseAsync()).IsTrue();
        await Assert.That(await mutex.ReleaseAsync()).IsFalse();
    }

    [Test]
    public async Task Extend_ProlongsTheKeyTtl()
    {
        await using var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(2));

        var beforeExtend = await Db.KeyTimeToLiveAsync(Key);
        await Assert.That(beforeExtend!.Value.TotalMilliseconds).IsLessThanOrEqualTo(2000);

        await Assert.That(await mutex!.ExtendAsync(TimeSpan.FromSeconds(60))).IsTrue();

        var afterExtend = await Db.KeyTimeToLiveAsync(Key);
        await Assert.That(afterExtend!.Value.TotalMilliseconds).IsGreaterThan(30_000);
        await Assert.That(mutex.Expiry).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task Extend_AfterRelease_ReturnsFalseAndDoesNotRecreateTheKey()
    {
        var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));
        await mutex!.ReleaseAsync();

        await Assert.That(await mutex.ExtendAsync(TimeSpan.FromSeconds(60))).IsFalse();
        await Assert.That(await Db.KeyExistsAsync(Key)).IsFalse();
    }

    [Test]
    public async Task Dispose_ReleasesTheLock()
    {
        var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));

        await mutex!.DisposeAsync();

        await Assert.That(await Db.KeyExistsAsync(Key)).IsFalse();
    }

    [Test]
    public async Task Dispose_AfterExplicitRelease_DoesNotTouchTheNextOwnersLock()
    {
        var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));
        await Assert.That(await mutex!.ReleaseAsync()).IsTrue();

        await using var next = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));
        await Assert.That(next).IsNotNull();

        await mutex.DisposeAsync();

        var stored = (byte[]?)await Db.StringGetAsync(Key);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.AsSpan().SequenceEqual(next!.Token.Span)).IsTrue();
    }

    [Test]
    public async Task StolenLock_FailsExtendAndRelease_AndDisposeLeavesTheThiefAlone()
    {
        var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));

        // Simulate the lease expiring and another owner taking over.
        await Db.StringSetAsync(Key, "thief", TimeSpan.FromSeconds(30));

        await Assert.That(await mutex!.ExtendAsync(TimeSpan.FromSeconds(60))).IsFalse();
        await Assert.That(await mutex.ReleaseAsync()).IsFalse();

        await mutex.DisposeAsync();

        await Assert.That((string?)await Db.StringGetAsync(Key)).IsEqualTo("thief");
    }

    [Test]
    public async Task Dispose_LeavesTheThiefAlone_WhenReleaseWasNeverCalled()
    {
        var mutex = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));
        await Db.StringSetAsync(Key, "thief", TimeSpan.FromSeconds(30));

        await mutex!.DisposeAsync();

        await Assert.That((string?)await Db.StringGetAsync(Key)).IsEqualTo("thief");
    }

    [Test]
    public async Task Acquire_AppliesTheClientKeyPrefix()
    {
        var prefixed = Client.WithKeyPrefix("tenant:");

        await using var mutex = await prefixed.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));

        await Assert.That(mutex).IsNotNull();
        await Assert.That(await Db.KeyExistsAsync("tenant:" + Key)).IsTrue();
        await Assert.That(await Db.KeyExistsAsync(Key)).IsFalse();

        await Assert.That(await mutex!.ReleaseAsync()).IsTrue();
        await Assert.That(await Db.KeyExistsAsync("tenant:" + Key)).IsFalse();
    }

    [Test]
    public async Task AcquireWithWait_ReturnsNullWhenTheLockIsHeldForTheWholeBudget()
    {
        await using var holder = await Client.Locks.AcquireAsync(Key, TimeSpan.FromSeconds(30));
        await Assert.That(holder).IsNotNull();

        var start = Stopwatch.GetTimestamp();
        var contender = await Client.Locks.AcquireAsync(
            Key,
            TimeSpan.FromSeconds(30),
            wait: TimeSpan.FromMilliseconds(400),
            retryEvery: TimeSpan.FromMilliseconds(50));

        await Assert.That(contender).IsNull();
        await Assert.That(Stopwatch.GetElapsedTime(start)).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300));
    }

    [Test]
    public async Task AcquireWithWait_SucceedsOnceTheHoldersLeaseExpires()
    {
        var holder = await Client.Locks.AcquireAsync(Key, TimeSpan.FromMilliseconds(300));
        await Assert.That(holder).IsNotNull();

        await using var contender = await Client.Locks.AcquireAsync(
            Key,
            TimeSpan.FromSeconds(30),
            wait: TimeSpan.FromSeconds(10),
            retryEvery: TimeSpan.FromMilliseconds(50));

        await Assert.That(contender).IsNotNull();

        // The first lease expired, so its handle owns nothing and must not delete the new lock.
        await Assert.That(await holder!.ReleaseAsync()).IsFalse();

        var stored = (byte[]?)await Db.StringGetAsync(Key);
        await Assert.That(Encoding.UTF8.GetString(stored!)).IsEqualTo(Encoding.UTF8.GetString(contender!.Token.Span));
    }

    [Test]
    public async Task AcquireWithWait_RejectsANonPositiveRetryInterval()
    {
        await Assert.That(async () => await Client.Locks.AcquireAsync(
                Key,
                TimeSpan.FromSeconds(30),
                wait: TimeSpan.FromSeconds(1),
                retryEvery: TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
    }
}
