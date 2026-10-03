using Respire.Coordination;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ReadWriteLockIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task MaximumDurationUsesValidRedisExpiryArgument()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var coordination = new RespireCoordination(client);
        await using var attempt = await coordination.TryAcquireWriteLockAsync(
            $"rwlock:max-duration:{Guid.NewGuid():N}", TimeSpan.MaxValue);

        await Assert.That(attempt.Acquired).IsTrue();
    }
}
