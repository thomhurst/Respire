using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireKeyTests
{
    [Test]
    public async Task EmptyAndEqualityOperatorsUseValueSemantics()
    {
        RespireKey text = "key";
        RespireKey bytes = "key"u8.ToArray();

        await Assert.That(RespireKey.Empty.IsEmpty).IsTrue();
        await Assert.That(text == bytes).IsTrue();
        await Assert.That(text != bytes).IsFalse();
        await Assert.That(text != "other").IsTrue();
    }

    [Test]
    [Arguments("key")]
    [Arguments("£ sterling")]
    [Arguments("𐍈")]
    [Arguments("�")]
    public async Task EquivalentRepresentations_AreEqualAndShareHashCode(string value)
    {
        var stringKey = new RespireKey(value);
        var bytesKey = new RespireKey(Encoding.UTF8.GetBytes(value));

        await Assert.That(stringKey.Equals(bytesKey)).IsTrue();
        await Assert.That(bytesKey.Equals(stringKey)).IsTrue();
        await Assert.That(stringKey.GetHashCode()).IsEqualTo(bytesKey.GetHashCode());
    }

    [Test]
    public async Task InvalidUtf8_DoesNotEqualReplacementCharacter()
    {
        var invalidBytes = new RespireKey(new byte[] { 0xFF });
        var replacementString = new RespireKey("�");

        await Assert.That(invalidBytes.Equals(replacementString)).IsFalse();
        await Assert.That(replacementString.Equals(invalidBytes)).IsFalse();
    }

    [Test]
    public async Task LongEquivalentRepresentations_AreEqualAndShareHashCode()
    {
        var value = new string('x', 1024) + "£";
        var stringKey = new RespireKey(value);
        var bytesKey = new RespireKey(Encoding.UTF8.GetBytes(value));

        await Assert.That(stringKey.Equals(bytesKey)).IsTrue();
        await Assert.That(bytesKey.Equals(stringKey)).IsTrue();
        await Assert.That(stringKey.GetHashCode()).IsEqualTo(bytesKey.GetHashCode());
    }
}
