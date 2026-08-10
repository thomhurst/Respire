using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

/// <summary>
/// The expiry union used by expiry mutations and SET-style writes: exactly one of none, relative,
/// absolute, keep, or persist,
/// with implicit conversions so <c>expiry: TimeSpan.FromMinutes(5)</c> keeps working.
/// </summary>
public class RespireExpiryTests
{
    [Test]
    public async Task Default_IsNone()
    {
        RespireExpiry ttl = default;

        await Assert.That(ttl).IsEqualTo(RespireExpiry.None);
        await Assert.That(ttl.IsNone).IsTrue();
        await Assert.That(ttl.IsKeep).IsFalse();
        await Assert.That(ttl.IsPersist).IsFalse();
        await Assert.That(ttl.TimeToLive).IsNull();
        await Assert.That(ttl.ExpiresAt).IsNull();
    }

    [Test]
    public async Task In_CarriesTheRelativeTimeToLive()
    {
        var ttl = RespireExpiry.In(TimeSpan.FromMinutes(5));

        await Assert.That(ttl.TimeToLive).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(ttl.ExpiresAt).IsNull();
        await Assert.That(ttl.IsNone).IsFalse();
        await Assert.That(ttl.IsKeep).IsFalse();
    }

    [Test]
    public async Task In_TruncatesToMilliseconds()
    {
        var ttl = RespireExpiry.In(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * 3 + 5000));

        await Assert.That(ttl.TimeToLive).IsEqualTo(TimeSpan.FromMilliseconds(3));
    }

    [Test]
    public async Task At_CarriesTheAbsoluteInstant()
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var ttl = RespireExpiry.At(instant);

        await Assert.That(ttl.ExpiresAt).IsEqualTo(instant);
        await Assert.That(ttl.TimeToLive).IsNull();
        await Assert.That(ttl.IsNone).IsFalse();
    }

    [Test]
    public async Task Keep_IsItsOwnState()
    {
        await Assert.That(RespireExpiry.Keep.IsKeep).IsTrue();
        await Assert.That(RespireExpiry.Keep.IsNone).IsFalse();
        await Assert.That(RespireExpiry.Keep.TimeToLive).IsNull();
        await Assert.That(RespireExpiry.Keep.ExpiresAt).IsNull();
        await Assert.That(RespireExpiry.Keep).IsNotEqualTo(RespireExpiry.None);
    }

    [Test]
    public async Task Persist_IsItsOwnState()
    {
        await Assert.That(RespireExpiry.Persist.IsPersist).IsTrue();
        await Assert.That(RespireExpiry.Persist.IsNone).IsFalse();
        await Assert.That(RespireExpiry.Persist.IsKeep).IsFalse();
        await Assert.That(RespireExpiry.Persist.TimeToLive).IsNull();
        await Assert.That(RespireExpiry.Persist.ExpiresAt).IsNull();
        await Assert.That(RespireExpiry.Persist).IsNotEqualTo(RespireExpiry.None);
        await Assert.That(RespireExpiry.Persist).IsNotEqualTo(RespireExpiry.Keep);
    }

    [Test]
    public async Task ImplicitConversion_FromTimeSpan_IsRelative()
    {
        RespireExpiry ttl = TimeSpan.FromSeconds(30);

        await Assert.That(ttl).IsEqualTo(RespireExpiry.In(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task ImplicitConversion_FromNullableTimeSpan_MapsNullToNone()
    {
        TimeSpan? missing = null;
        TimeSpan? present = TimeSpan.FromSeconds(30);

        RespireExpiry none = missing;
        RespireExpiry relative = present;

        await Assert.That(none).IsEqualTo(RespireExpiry.None);
        await Assert.That(relative).IsEqualTo(RespireExpiry.In(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task ImplicitConversion_FromDateTimeOffset_IsAbsolute()
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        RespireExpiry ttl = instant;

        await Assert.That(ttl).IsEqualTo(RespireExpiry.At(instant));
    }

    [Test]
    public async Task Equality_DistinguishesKindAndValue()
    {
        var relative = RespireExpiry.In(TimeSpan.FromMilliseconds(1000));
        var absolute = RespireExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(1000));

        await Assert.That(relative == RespireExpiry.In(TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(relative.GetHashCode()).IsEqualTo(RespireExpiry.In(TimeSpan.FromSeconds(1)).GetHashCode());
        await Assert.That(relative != absolute).IsTrue();
        await Assert.That(relative != RespireExpiry.In(TimeSpan.FromSeconds(2))).IsTrue();
        await Assert.That(relative.Equals((object)RespireExpiry.In(TimeSpan.FromSeconds(1)))).IsTrue();
        await Assert.That(relative.Equals("not a ttl")).IsFalse();
    }

    [Test]
    public async Task ToString_NamesTheState()
    {
        await Assert.That(RespireExpiry.None.ToString()).IsEqualTo("(none)");
        await Assert.That(RespireExpiry.Keep.ToString()).IsEqualTo("(keep)");
        await Assert.That(RespireExpiry.Persist.ToString()).IsEqualTo("(persist)");
        await Assert.That(RespireExpiry.In(TimeSpan.FromSeconds(1)).ToString())
            .IsEqualTo(TimeSpan.FromSeconds(1).ToString());
    }

    [Test]
    public async Task Size_StaysSmallEnoughToStayOffTheHeap()
    {
        // A long plus a byte kind — the SET hot path must not grow an allocation for the expiry.
        await Assert.That(Unsafe.SizeOf<RespireExpiry>()).IsEqualTo(16);
    }
}
