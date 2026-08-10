using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

/// <summary>
/// The expiry union used by SET-style writes: exactly one of none, relative, absolute, or keep,
/// with implicit conversions so <c>expiry: TimeSpan.FromMinutes(5)</c> keeps working.
/// </summary>
public class RespireTtlTests
{
    [Test]
    public async Task Default_IsNone()
    {
        RespireTtl ttl = default;

        await Assert.That(ttl).IsEqualTo(RespireTtl.None);
        await Assert.That(ttl.IsNone).IsTrue();
        await Assert.That(ttl.IsKeep).IsFalse();
        await Assert.That(ttl.TimeToLive).IsNull();
        await Assert.That(ttl.ExpiresAt).IsNull();
    }

    [Test]
    public async Task In_CarriesTheRelativeTimeToLive()
    {
        var ttl = RespireTtl.In(TimeSpan.FromMinutes(5));

        await Assert.That(ttl.TimeToLive).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(ttl.ExpiresAt).IsNull();
        await Assert.That(ttl.IsNone).IsFalse();
        await Assert.That(ttl.IsKeep).IsFalse();
    }

    [Test]
    public async Task In_TruncatesToMilliseconds()
    {
        var ttl = RespireTtl.In(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * 3 + 5000));

        await Assert.That(ttl.TimeToLive).IsEqualTo(TimeSpan.FromMilliseconds(3));
    }

    [Test]
    public async Task At_CarriesTheAbsoluteInstant()
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var ttl = RespireTtl.At(instant);

        await Assert.That(ttl.ExpiresAt).IsEqualTo(instant);
        await Assert.That(ttl.TimeToLive).IsNull();
        await Assert.That(ttl.IsNone).IsFalse();
    }

    [Test]
    public async Task Keep_IsItsOwnState()
    {
        await Assert.That(RespireTtl.Keep.IsKeep).IsTrue();
        await Assert.That(RespireTtl.Keep.IsNone).IsFalse();
        await Assert.That(RespireTtl.Keep.TimeToLive).IsNull();
        await Assert.That(RespireTtl.Keep.ExpiresAt).IsNull();
        await Assert.That(RespireTtl.Keep).IsNotEqualTo(RespireTtl.None);
    }

    [Test]
    public async Task ImplicitConversion_FromTimeSpan_IsRelative()
    {
        RespireTtl ttl = TimeSpan.FromSeconds(30);

        await Assert.That(ttl).IsEqualTo(RespireTtl.In(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task ImplicitConversion_FromNullableTimeSpan_MapsNullToNone()
    {
        TimeSpan? missing = null;
        TimeSpan? present = TimeSpan.FromSeconds(30);

        RespireTtl none = missing;
        RespireTtl relative = present;

        await Assert.That(none).IsEqualTo(RespireTtl.None);
        await Assert.That(relative).IsEqualTo(RespireTtl.In(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task ImplicitConversion_FromDateTimeOffset_IsAbsolute()
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        RespireTtl ttl = instant;

        await Assert.That(ttl).IsEqualTo(RespireTtl.At(instant));
    }

    [Test]
    public async Task Equality_DistinguishesKindAndValue()
    {
        var relative = RespireTtl.In(TimeSpan.FromMilliseconds(1000));
        var absolute = RespireTtl.At(DateTimeOffset.FromUnixTimeMilliseconds(1000));

        await Assert.That(relative == RespireTtl.In(TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(relative.GetHashCode()).IsEqualTo(RespireTtl.In(TimeSpan.FromSeconds(1)).GetHashCode());
        await Assert.That(relative != absolute).IsTrue();
        await Assert.That(relative != RespireTtl.In(TimeSpan.FromSeconds(2))).IsTrue();
        await Assert.That(relative.Equals((object)RespireTtl.In(TimeSpan.FromSeconds(1)))).IsTrue();
        await Assert.That(relative.Equals("not a ttl")).IsFalse();
    }

    [Test]
    public async Task ToString_NamesTheState()
    {
        await Assert.That(RespireTtl.None.ToString()).IsEqualTo("(none)");
        await Assert.That(RespireTtl.Keep.ToString()).IsEqualTo("(keep)");
        await Assert.That(RespireTtl.In(TimeSpan.FromSeconds(1)).ToString())
            .IsEqualTo(TimeSpan.FromSeconds(1).ToString());
    }

    [Test]
    public async Task Size_StaysSmallEnoughToStayOffTheHeap()
    {
        // A long plus a byte kind — the SET hot path must not grow an allocation for the expiry.
        await Assert.That(Unsafe.SizeOf<RespireTtl>()).IsEqualTo(16);
    }
}
