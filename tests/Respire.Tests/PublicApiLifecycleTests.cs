using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class PublicApiLifecycleTests
{
    [Test]
    public async Task PublicApi_ObsoleteMembersHaveDiagnosticAndMigrationLink()
    {
        var obsolete = typeof(RespireClient).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Prepend(type))
            .Where(member => member.GetCustomAttribute<ObsoleteAttribute>(inherit: false) is { } attribute &&
                             !HasMigrationDiagnostic(attribute))
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .ToArray();

        await Assert.That(obsolete).IsEmpty();
    }

    [Test]
    [Arguments("RESPIRE001", "https://thomhurst.github.io/Respire/docs/migrations/{0}", true)]
    [Arguments(null, "https://thomhurst.github.io/Respire/docs/migrations/{0}", false)]
    [Arguments(" ", "https://thomhurst.github.io/Respire/docs/migrations/{0}", false)]
    [Arguments("RESPIRE001", null, false)]
    [Arguments("RESPIRE001", "", false)]
    [Arguments("RESPIRE001", "/docs/migrations/{0}", false)]
    [Arguments("RESPIRE001", "https://example.com/migrations", false)]
    [Arguments("RESPIRE001", "https://example.com/migrations/{1}", false)]
    [Arguments("RESPIRE001", "https://example.com/migrations/{0}/{1}", false)]
    [Arguments("RESPIRE001", "https://example.com/migrations/{{0}}", false)]
    [Arguments("RESPIRE001", "http://example.com/migrations/{0}", false)]
    public async Task DeprecationPolicy_RequiresDiagnosticAndMigrationUrl(
        string? diagnosticId, string? urlFormat, bool expected)
    {
        var attribute = new ObsoleteAttribute("Use the replacement API.")
        {
            DiagnosticId = diagnosticId,
            UrlFormat = urlFormat
        };

        await Assert.That(HasMigrationDiagnostic(attribute)).IsEqualTo(expected);
    }

    private static bool HasMigrationDiagnostic(ObsoleteAttribute attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute.DiagnosticId) ||
            attribute.UrlFormat is not { } format || !format.Contains("{0}", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return System.Text.CompositeFormat.Parse(format).MinimumArgumentCount == 1 &&
                   Uri.TryCreate(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    format, attribute.DiagnosticId), UriKind.Absolute, out var uri) &&
                   uri.Scheme == Uri.UriSchemeHttps;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
