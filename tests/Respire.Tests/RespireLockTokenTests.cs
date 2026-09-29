using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireLockTokenTests
{
    [Test]
    [Arguments(0xD800)]
    [Arguments(0xDC00)]
    public async Task Text_RejectsUnpairedSurrogates(int codeUnit)
    {
        var invalid = new string((char)codeUnit, 1);
        await Assert.That(() => new RespireLockToken(invalid)).ThrowsExactly<EncoderFallbackException>();
        await Assert.That(() =>
        {
            RespireLockToken token = invalid;
            return token;
        }).ThrowsExactly<EncoderFallbackException>();
    }

    [Test]
    [Arguments("owner-雪")]
    [Arguments("owner-\U0001F512")]
    [Arguments("owner-\uFFFD")]
    public async Task TextAndBytesHaveIdenticalEqualityAndHashCodes(string value)
    {
        RespireLockToken text = value;
        var bytes = new RespireLockToken(Encoding.UTF8.GetBytes(value));

        await Assert.That(text == bytes).IsTrue();
        await Assert.That(text.Equals((object)bytes)).IsTrue();
        await Assert.That(text.GetHashCode()).IsEqualTo(bytes.GetHashCode());
        await Assert.That(bytes.ToUtf8String()).IsEqualTo(value);
        await Assert.That(text != (RespireLockToken)"other").IsTrue();
    }

    [Test]
    public async Task BinaryTokensPreserveSlicesAndSnapshotCallerStorage()
    {
        byte[] storage = [1, 0xff, 0, 0xfe, 2];
        var token = new RespireLockToken(storage.AsMemory(1, 3));
        var hash = token.GetHashCode();
        storage[1] = 0;

        await Assert.That(token.Bytes.Span.SequenceEqual(new byte[] { 0xff, 0, 0xfe })).IsTrue();
        await Assert.That(token.GetHashCode()).IsEqualTo(hash);
        await Assert.That(token == (RespireLockToken)new byte[] { 0xff, 0, 0xfe }).IsTrue();
        await Assert.That(token != (RespireLockToken)new byte[] { 0xfe, 0, 0xff }).IsTrue();
    }

    [Test]
    public async Task BinaryDisplayIsLosslessAndTextDecodingRejectsInvalidUtf8()
    {
        var first = new RespireLockToken(new byte[] { 0xff, 0 });
        var second = new RespireLockToken(new byte[] { 0xfe, 0 });
        await Assert.That(first.ToString()).IsEqualTo("FF00");
        await Assert.That(second.ToString()).IsEqualTo("FE00");
        await Assert.That(() => first.ToUtf8String()).Throws<DecoderFallbackException>();
        await Assert.That(default(RespireLockToken).ToString()).IsEqualTo("");
    }

    [Test]
    public async Task NullInputsAreRejectedAtConstruction()
    {
        await Assert.That(() => new RespireLockToken((string)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => (RespireLockToken)(byte[])null!).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task DefaultAndEmptyTokensCompareEqually()
    {
        RespireLockToken empty = "";
        await Assert.That(default(RespireLockToken).IsEmpty).IsTrue();
        await Assert.That(empty == default).IsTrue();
        await Assert.That(empty.GetHashCode()).IsEqualTo(default(RespireLockToken).GetHashCode());
    }
}
