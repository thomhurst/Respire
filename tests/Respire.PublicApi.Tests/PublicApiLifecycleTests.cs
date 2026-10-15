using System.Reflection;
using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.PublicApi.Tests;

public class PublicApiLifecycleTests
{
    [Test]
    public async Task PublicApi_ObsoleteMembersHaveDiagnosticAndMigrationLink()
    {
        using var manifest = typeof(PublicApiLifecycleTests).Assembly.GetManifestResourceStream("ShippingAssemblies")
            ?? throw new InvalidOperationException("The shipping assembly manifest is missing.");
        using var reader = new StreamReader(manifest);
        var names = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var assemblies = LoadExpectedAssemblies(AppContext.BaseDirectory, names);

        await Assert.That(assemblies.SelectMany(FindInvalidDeprecations).ToArray()).IsEmpty();
    }

    [Test]
    public void PublicApi_RejectsMissingShippingAssembly()
    {
        Assert.Throws<FileNotFoundException>(() =>
            LoadExpectedAssemblies(AppContext.BaseDirectory, ["Respire", "Respire.MissingShippingAssembly"]));
    }

    [Test]
    public void PublicApi_RejectsEmptyInventory()
    {
        Assert.Throws<InvalidOperationException>(() => LoadExpectedAssemblies(AppContext.BaseDirectory, []));
    }

    [Test]
    public async Task PublicApi_DetectsInvalidMetadataOutsideCoreAssembly()
    {
        var fixtureAssembly = LoadExpectedAssemblies(AppContext.BaseDirectory,
            [typeof(PublicApiLifecycleTests).Assembly.GetName().Name!]).Single();
        await Assert.That(fixtureAssembly == typeof(RespireClient).Assembly).IsFalse();
        var invalid = FindInvalidDeprecations(fixtureAssembly);
        var prefix = $"{fixtureAssembly.GetName().Name}: Respire.PublicApi.Tests.";
        string[] expected =
        [
            prefix + "InvalidDeprecatedType",
            prefix + "DeprecationFixtures..ctor",
            prefix + "DeprecationFixtures.InvalidMethod",
            prefix + "DeprecationFixtures.InvalidProperty",
            prefix + "DeprecationFixtures.InvalidField",
            prefix + "DeprecationFixtures.InvalidEvent",
            prefix + "DeprecatedRequiredMembersFixture..ctor"
        ];
        // Exact comparison also proves valid obsolete APIs and private members are accepted.
        await Assert.That(invalid).IsEquivalentTo(expected);
    }

    private static Assembly[] LoadExpectedAssemblies(string directory, string[] names)
    {
        if (names.Length == 0)
        {
            throw new InvalidOperationException("The shipping assembly inventory must not be empty.");
        }

        return names.Select(name =>
        {
            var path = Path.Combine(directory, name + ".dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Expected shipping assembly '{name}' is missing from validation.", path);
            }

            return System.Reflection.Assembly.LoadFrom(path);
        }).ToArray();
    }

    private static string[] FindInvalidDeprecations(Assembly assembly) =>
        assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Prepend(type))
            .Where(member => member.GetCustomAttribute<ObsoleteAttribute>(inherit: false) is { } attribute &&
                             !IsRequiredMembersCompatibilityMarker(member, attribute) &&
                             !HasMigrationDiagnostic(attribute))
            .Select(member => $"{assembly.GetName().Name}: " +
                              (member is Type type ? type.FullName : $"{member.DeclaringType?.FullName}.{member.Name}"))
            .Distinct()
            .ToArray();

    // C# emits this exact Obsolete error to prevent older compilers from bypassing required members.
    // Authored Obsolete attributes on the same constructors must still follow the deprecation policy.
    // https://github.com/dotnet/csharplang/blob/main/proposals/csharp-11.0/required-members.md#metadata-representation
    private static bool IsRequiredMembersCompatibilityMarker(MemberInfo member, ObsoleteAttribute attribute) =>
        member is ConstructorInfo && attribute.IsError && attribute.DiagnosticId is null && attribute.UrlFormat is null &&
        attribute.Message == "Constructors of types with required members are not supported in this version of your compiler." &&
        member.GetCustomAttributes<CompilerFeatureRequiredAttribute>(inherit: false)
            .Any(feature => feature.FeatureName == CompilerFeatureRequiredAttribute.RequiredMembers);

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
