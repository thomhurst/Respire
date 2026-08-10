using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireValueTests
{
    [Test]
    public async Task NullStringAndByteArray_ConvertToNullValue()
    {
        string? nullString = null;
        byte[]? nullBytes = null;

        RespireValue stringValue = nullString;
        RespireValue bytesValue = nullBytes;

        await Assert.That(stringValue.IsNull).IsTrue();
        await Assert.That(bytesValue.IsNull).IsTrue();
    }

    [Test]
    public async Task EqualityUsesRedisWireRepresentationAcrossKinds()
    {
        RespireValue integer = 5;
        RespireValue text = "5";
        RespireValue bytes = "5"u8.ToArray();
        RespireValue floatingPoint = 5d;

        await Assert.That(integer == text).IsTrue();
        await Assert.That(text == bytes).IsTrue();
        await Assert.That(bytes == floatingPoint).IsTrue();
        await Assert.That(integer.GetHashCode()).IsEqualTo(bytes.GetHashCode());
        await Assert.That(integer != (RespireValue)6).IsTrue();
        await Assert.That(integer.Equals((object)text)).IsTrue();
        await Assert.That(integer.Equals((object)"5")).IsFalse();
    }

    [Test]
    public async Task NullOnlyEqualsNull()
    {
        await Assert.That(default(RespireValue) == RespireValue.Null).IsTrue();
        await Assert.That(RespireValue.Null != (RespireValue)"").IsTrue();
    }

    [Test]
    public async Task InvalidUtf8DoesNotEqualReplacementCharacter()
    {
        RespireValue invalidBytes = new byte[] { 0xFF };
        RespireValue replacementText = "�";

        await Assert.That(invalidBytes == replacementText).IsFalse();
    }

    [Test]
    public async Task LongTextAndBytesAreEqualAndShareHashCode()
    {
        var text = new string('x', 1024) + "£";
        RespireValue stringValue = text;
        RespireValue bytesValue = System.Text.Encoding.UTF8.GetBytes(text);

        await Assert.That(stringValue == bytesValue).IsTrue();
        await Assert.That(stringValue.GetHashCode()).IsEqualTo(bytesValue.GetHashCode());
    }

    [Test]
    public async Task CommonFrameworkTypesHaveDocumentedInvariantEncodings()
    {
        var guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var instant = new DateTimeOffset(2026, 8, 10, 12, 34, 56, 789, TimeSpan.FromHours(2));
        var duration = TimeSpan.FromDays(1) + TimeSpan.FromMilliseconds(234);

        RespireValue guidValue = guid;
        RespireValue instantValue = instant;
        RespireValue durationValue = duration;
        RespireValue characterValue = '£';

        await Assert.That(guidValue.ToString()).IsEqualTo(guid.ToString("D"));
        await Assert.That(instantValue.ToString()).IsEqualTo(instant.ToString("O", CultureInfo.InvariantCulture));
        await Assert.That(durationValue.ToString()).IsEqualTo(duration.ToString("c", CultureInfo.InvariantCulture));
        await Assert.That(characterValue.ToString()).IsEqualTo(((ushort)'£').ToString());
    }

    [Test]
    public async Task MemorySegmentsAndKeysConvertWithoutLosingTheirSlice()
    {
        var source = "xvaluey"u8.ToArray();
        RespireValue memory = source.AsMemory(1, 5);
        RespireValue segment = new ArraySegment<byte>(source, 1, 5);
        RespireKey key = "value";
        RespireValue keyValue = key;

        await Assert.That(memory == (RespireValue)"value").IsTrue();
        await Assert.That(segment == memory).IsTrue();
        await Assert.That(keyValue == memory).IsTrue();
    }

    [Test]
    public async Task LargeBinaryValuesCompareWithoutChangingTheirContents()
    {
        var left = Enumerable.Repeat((byte)0xA5, 1024 * 1024).ToArray();
        var right = left.ToArray();
        RespireValue leftValue = left;
        RespireValue rightValue = right;

        await Assert.That(leftValue == rightValue).IsTrue();
        await Assert.That(leftValue.GetHashCode()).IsEqualTo(rightValue.GetHashCode());
        await Assert.That(left.All(value => value == 0xA5)).IsTrue();
        await Assert.That(right.All(value => value == 0xA5)).IsTrue();
    }
}
