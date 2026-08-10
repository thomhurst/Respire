using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireStreamIdTests
{
    [Test]
    public async Task CompareTo_UsesNumericMillisecondsThenSequence()
    {
        var earlierMillisecond = new RespireStreamId("9-999");
        var laterMillisecond = new RespireStreamId("10-0");
        var laterSequence = new RespireStreamId("10-1");

        await Assert.That(earlierMillisecond.CompareTo(laterMillisecond)).IsLessThan(0);
        await Assert.That(laterMillisecond.CompareTo(laterSequence)).IsLessThan(0);
        await Assert.That(laterSequence.CompareTo(laterSequence)).IsEqualTo(0);
    }

    [Test]
    public async Task ComparisonOperators_FollowNumericOrderAndExactEquality()
    {
        var first = new RespireStreamId("10-1");
        var same = new RespireStreamId("10-1");
        var second = new RespireStreamId("10-2");

        await Assert.That(first < second).IsTrue();
        await Assert.That(first <= same).IsTrue();
        await Assert.That(second > first).IsTrue();
        await Assert.That(second >= same).IsTrue();
        await Assert.That(first == same).IsTrue();
        await Assert.That(first != second).IsTrue();
    }

    [Test]
    public async Task RangeSentinels_SortAtNumericExtremes()
    {
        var id = new RespireStreamId("1-0");

        await Assert.That(RespireStreamId.Min < id).IsTrue();
        await Assert.That(RespireStreamId.Max > id).IsTrue();
    }

    [Test]
    public async Task NewSentinel_CannotBeComparedWithoutServerState()
    {
        var id = new RespireStreamId("1-0");

        await Assert.That(() => RespireStreamId.New.CompareTo(id))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => RespireStreamId.New.CompareTo(RespireStreamId.New))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task InvalidSyntax_CannotBeCompared()
    {
        var invalid = new RespireStreamId("not-an-id");

        await Assert.That(() => invalid.CompareTo(RespireStreamId.Beginning))
            .ThrowsExactly<FormatException>();
        await Assert.That(() => invalid.CompareTo(invalid))
            .ThrowsExactly<FormatException>();
    }
}
