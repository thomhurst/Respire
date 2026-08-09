using System.Security.Cryptography;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireScriptTests
{
    [Test]
    [Arguments("return 1", "e0e1f9fabfc9d4800c877a703b823ac0578ff8db")]
    [Arguments("return '£'", "62f2fcd106a605e2d7b3dff8fb147ae6628b1a5c")]
    public async Task Create_ComputesLowercaseUtf8Sha1(string source, string expected)
    {
        var script = RespireScript.Create(source);

        await Assert.That(script.Source).IsEqualTo(source);
        await Assert.That(script.Sha1).IsEqualTo(expected);
    }

    [Test]
    public async Task Create_ComputesSha1ForLongSource()
    {
        var source = new string('x', 1024);
        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

        var script = RespireScript.Create(source);

        await Assert.That(script.Sha1).IsEqualTo(expected);
    }
}
