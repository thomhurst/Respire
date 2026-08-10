namespace Respire.Tests;

public class RespireTtlTests
{
    [Test]
    [Arguments(-2, false, false, null)]
    [Arguments(-1, true, false, null)]
    [Arguments(1500, true, true, 1500)]
    public async Task RedisTtlSentinelsAreRepresentedWithoutLosingExistence(
        long response, bool exists, bool hasExpiry, int? expectedMilliseconds)
    {
        var ttl = RespireTtl.FromRedisMilliseconds(response);

        await Assert.That(ttl.Exists).IsEqualTo(exists);
        await Assert.That(ttl.HasExpiry).IsEqualTo(hasExpiry);
        await Assert.That(ttl.TimeToLive?.TotalMilliseconds).IsEqualTo(expectedMilliseconds);
    }

    [Test]
    public async Task ToStringUsesNeutralMissingDescription()
    {
        await Assert.That(RespireTtl.FromRedisMilliseconds(-2).ToString()).IsEqualTo("(missing)");
        await Assert.That(RespireTtl.FromRedisMilliseconds(-1).ToString()).IsEqualTo("(no expiry)");
    }
}
