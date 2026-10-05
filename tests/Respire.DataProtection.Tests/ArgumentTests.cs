using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.DataProtection.Tests;

public class ArgumentTests
{
    [Test]
    public async Task RejectsNullFactoryAndBuilder()
    {
        await Assert.That(() => new RespireXmlRepository(null!, "keys")).Throws<ArgumentNullException>();
        var builder = new ServiceCollection().AddDataProtection();
        await Assert.That(() => builder.PersistKeysToRespire(null!, "keys")).Throws<ArgumentNullException>();
        await Assert.That(() => RespireDataProtectionBuilderExtensions.PersistKeysToRespire(
            null!, () => throw new InvalidOperationException(), "keys")).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task RejectsNullElementBeforeAccessingClient()
    {
        var repository = new RespireXmlRepository(() => throw new InvalidOperationException(), "keys");
        await Assert.That(() => repository.StoreElement(null!, "key")).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task PropagatesFactoryFailure()
    {
        var repository = new RespireXmlRepository(() => throw new InvalidOperationException("factory failed"), "keys");
        await Assert.That(() => repository.GetAllElements()).Throws<InvalidOperationException>();
        await Assert.That(() => repository.StoreElement(new XElement("key"), "key")).Throws<InvalidOperationException>();
    }
}
