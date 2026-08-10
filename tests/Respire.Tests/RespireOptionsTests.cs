using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireOptionsTests
{
    [Test]
    public async Task ConnectionString_DefaultsAllowAdminToFalse()
    {
        var hostOnly = RespireOptions.Parse("localhost");
        var uri = RespireOptions.Parse("redis://localhost");

        await Assert.That(hostOnly.AllowAdmin).IsFalse();
        await Assert.That(uri.AllowAdmin).IsFalse();
    }

    [Test]
    public async Task ConnectionString_ParsesAllowAdmin()
    {
        var enabled = RespireOptions.Parse("redis://localhost?allowAdmin=true");
        var disabled = RespireOptions.Parse("redis://localhost?allowAdmin=false");

        await Assert.That(enabled.AllowAdmin).IsTrue();
        await Assert.That(disabled.AllowAdmin).IsFalse();
    }
}
