using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class PublicApiLifecycleTests
{
    [Test]
    public async Task PreReleasePublicApi_HasNoObsoleteMembers()
    {
        var obsolete = typeof(RespireClient).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Prepend(type))
            .Where(member => member.IsDefined(typeof(ObsoleteAttribute), inherit: false))
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .ToArray();

        await Assert.That(obsolete).IsEmpty();
    }
}
