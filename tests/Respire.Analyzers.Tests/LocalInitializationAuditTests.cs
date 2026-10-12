using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Respire.Analyzers.Tests.LocalInitializationAudit;

namespace Respire.Analyzers.Tests;

/// <summary>
/// Guards the local-initialization audit for #1065. Every package stack allocation needs its
/// own justification for its current enclosing member, and the compiled assemblies must match
/// the reviewed inventory and policy.
/// </summary>
public class LocalInitializationAuditTests
{
    /// <summary>
    /// The reviewed SkipLocalsInit scopes per assembly. Empty: no package suppresses local
    /// initialization, because no measured scope has yet shown a benefit without regression.
    /// </summary>
    private static readonly HashSet<string> ReviewedSkipLocalsInitScopes = new(StringComparer.Ordinal);

    /// <summary>
    /// Stack allocations compiled into package assemblies from NuGet content sources, which carry
    /// no repository comment. Keyed by assembly, framework, method and localloc count, so a package
    /// upgrade that adds or moves a site fails until it is reviewed here.
    /// </summary>
    private static readonly Dictionary<string, string> ReviewedImportedSites = new(StringComparer.Ordinal)
    {
        // Polyfill 11.4.4, net8.0 only. Base64Url.Decode: quantum[i] is read only for i below count, after
        // DecodeValue wrote it. CreateVersion7: CopyTo writes bytes 0..6 and RandomNumberGenerator.Fill
        // writes 6..16 before the Guid reads all 16. HighNibble: Guid.TryWriteBytes writes all 16 bytes
        // before buffer[index] is read.
        ["Respire|net8.0|System.Buffers.Text.Base64Url::Decode``0(System.ReadOnlySpan`1<Char>,System.Span`1<Byte>,Boolean,Int32&,Int32&,Boolean):System.Buffers.OperationStatus|1"] = "Polyfill Base64Url.Decode quantum",
        ["Respire|net8.0|Polyfills.Polyfill::CreateVersion7``0(System.DateTimeOffset):System.Guid|1"] = "Polyfill Guid.CreateVersion7 uuidBytes",
        ["Respire|net8.0|Polyfills.Polyfill::HighNibble``0(System.Guid,Int32):Int32|1"] = "Polyfill Guid.Variant/Version buffer",
        ["Respire.SignalR|net8.0|System.Buffers.Text.Base64Url::Decode``0(System.ReadOnlySpan`1<Char>,System.Span`1<Byte>,Boolean,Int32&,Int32&,Boolean):System.Buffers.OperationStatus|1"] = "Polyfill Base64Url.Decode quantum",
        ["Respire.SignalR|net8.0|Polyfills.Polyfill::CreateVersion7``0(System.DateTimeOffset):System.Guid|1"] = "Polyfill Guid.CreateVersion7 uuidBytes",
        ["Respire.SignalR|net8.0|Polyfills.Polyfill::HighNibble``0(System.Guid,Int32):Int32|1"] = "Polyfill Guid.Variant/Version buffer",
    };

    private static readonly string Flavor =
#if DEBUG
        "DEBUG";
#else
        "RELEASE";
#endif

    private const string Reviewed = "// stackalloc-audit[{0}]: init: synthetic initialization control; read: synthetic read boundary control; failure: synthetic failure path control.";

    private static TestInspectionSource.SourceConfiguration[] AuditConfigurations()
        => Configurations(TestInspectionSource.ReadSourceConfigurations());

    private static IEnumerable<(string Package, string Path, string Text)> PackageSources()
        => TestInspectionSource.ReadLibrarySources()
            .Select(source => (Package: source.Path.Split('/')[1], Path: "src/" + source.Path["LibrarySource/".Length..], source.Text));

    [Test]
    public async Task EveryPackageStackAllocationHasACurrentJustification()
    {
        var configurations = AuditConfigurations();
        await Assert.That(configurations.Length).IsEqualTo(4);
        var sources = PackageSources().ToArray();
        await Assert.That(sources.Length).IsGreaterThan(100);
        await Assert.That(sources.Any(source => source.Package == "Respire.Analyzers")).IsTrue();
        var errors = sources.SelectMany(source => Validate(source.Path, source.Text, configurations)).ToArray();
        var sites = sources.Sum(source => FindSites(source.Path, source.Text, configurations).Length);
        // The audit found 39 sites when it was introduced; a sudden zero means the scan broke.
        await Assert.That(sites).IsGreaterThan(30);
        if (errors.Length != 0) throw new InvalidOperationException(string.Join('\n', errors));
    }

    [Test]
    public async Task CompiledPackagesMatchTheAuditedInventoryAndPolicy()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "local-initialization-audit");
        var assemblies = Directory.Exists(root) ? Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories) : [];
        // 28 packages for each of net8.0 and net10.0, staged by the project file.
        await Assert.That(assemblies.Length).IsGreaterThanOrEqualTo(56);
        await Assert.That(assemblies.Any(path => Path.GetFileName(path) == "Respire.Analyzers.dll"
            && Path.GetFileName(Path.GetDirectoryName(path)) == "netstandard2.0")).IsTrue();
        var configurations = AuditConfigurations();
        var sourceSites = PackageSources()
            .SelectMany(source => FindSites(source.Path, source.Text, configurations).Select(site => (source.Package, Site: site)))
            .ToArray();
        var errors = new List<string>();
        var imported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblies.Order(StringComparer.Ordinal))
        {
            var framework = Path.GetFileName(Path.GetDirectoryName(path)!);
            var (compiled, skipTargets) = InspectAssembly(path, framework);
            var assembly = Path.GetFileNameWithoutExtension(path);
            foreach (var target in skipTargets)
                if (!ReviewedSkipLocalsInitScopes.Contains(assembly + "|" + target))
                    errors.Add($"{assembly} ({framework}): unreviewed SkipLocalsInit on {target}.");
            foreach (var site in compiled.Where(site => !site.InitLocals))
                errors.Add($"{assembly} ({framework}): {site.Method} executes localloc without local initialization.");
            var own = compiled.Where(site => !IsImported(site.Method)).Sum(site => site.Count);
            var expected = sourceSites.Count(entry => entry.Package == assembly
                && entry.Site.Configurations.Contains(framework + "/" + Flavor, StringComparer.Ordinal));
            // The shipped analyzer has one netstandard2.0 build, with no framework-specific stack sites.
            if (assembly == "Respire.Analyzers")
                expected = sourceSites.Count(entry => entry.Package == assembly);
            if (own != expected)
                errors.Add($"{assembly} ({framework}): {own} compiled localloc site(s) in repository code but {expected} audited source site(s). "
                    + "Generated or imported code may allocate on the stack:\n    "
                    + string.Join("\n    ", compiled.Where(site => !IsImported(site.Method)).Select(site => $"{site.Method} x{site.Count}")));
            foreach (var site in compiled.Where(site => IsImported(site.Method)))
            {
                var key = $"{assembly}|{framework}|{site.Method}|{site.Count}";
                imported.Add(key);
                if (!ReviewedImportedSites.ContainsKey(key)) errors.Add("Unreviewed imported stack allocation: " + key);
            }
        }
        foreach (var stale in ReviewedImportedSites.Keys.Except(imported, StringComparer.Ordinal))
            errors.Add("Stale imported stack allocation review: " + stale);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join('\n', errors));
    }

    /// <summary>
    /// Repository and generated package code lives under the <c>Respire</c> and <c>Aspire.Respire</c>
    /// namespaces. Anything else in a package assembly was compiled from a NuGet content source,
    /// for example Polyfill's <c>Polyfills</c> and <c>System.*</c> types.
    /// </summary>
    private static bool IsImported(string method)
        => !method.StartsWith("Respire.", StringComparison.Ordinal) && !method.StartsWith("Aspire.Respire.", StringComparison.Ordinal);

    [Test]
    public async Task GeneratorsEmitNoStackAllocations()
    {
        var generators = TestInspectionSource.ReadLibrarySources()
            .Where(source => source.Path.StartsWith("LibrarySource/Respire.Analyzers/", StringComparison.Ordinal)).ToArray();
        await Assert.That(generators.Length).IsGreaterThan(5);
        // Generated code compiles into consumer modules, whose policy Respire does not control.
        // Emitting stackalloc needs its own audit; extend this guard before allowing it.
        var emitted = generators.SelectMany(source => FindEmittedStackAllocations(source.Path, source.Text)).ToArray();
        await Assert.That(emitted).IsEmpty();
        await Assert.That(FindEmittedStackAllocations("control.cs", "class C { string S = \"Span<byte> b = stackalloc byte[4];\"; }").Length).IsEqualTo(1);
        await Assert.That(FindEmittedStackAllocations("control.cs", "class C { string S = $\"\"\"\n  stackalloc {1}\n  \"\"\"; }").Length).IsEqualTo(1);
    }

    private const string Original = """
        class Example
        {
            void Read()
            {
                {0}
                Span<byte> buffer = stackalloc byte[8];
                buffer.Clear();
                Consume(buffer);
            }
        }
        """;

    private static string[] Check(string template, string? fingerprint = null)
    {
        var configurations = AuditConfigurations();
        var unreviewed = template.Replace("{0}", "", StringComparison.Ordinal);
        fingerprint ??= FindSites("src/Example.cs", unreviewed, configurations).Select(site => site.Fingerprint).Distinct().Single();
        return Validate("src/Example.cs", template.Replace("{0}", string.Format(System.Globalization.CultureInfo.InvariantCulture, Reviewed, fingerprint), StringComparison.Ordinal), configurations);
    }

    private static string FingerprintOf(string template)
        => FindSites("src/Example.cs", template.Replace("{0}", "", StringComparison.Ordinal), AuditConfigurations()).Select(site => site.Fingerprint).Distinct().Single();

    [Test]
    public async Task ReviewedSitePasses() => await Assert.That(Check(Original)).IsEmpty();

    [Test]
    public async Task MissingJustificationFails()
    {
        var errors = Validate("src/Example.cs", Original.Replace("{0}", "", StringComparison.Ordinal), AuditConfigurations());
        await Assert.That(errors.Single()).Contains("need 1");
    }

    [Test]
    [Arguments("Span<byte> buffer = stackalloc byte[16];")]
    [Arguments("Span<byte> buffer = stackalloc byte[8]; Span<byte> added = stackalloc byte[1];")]
    [Arguments("Span<int> buffer = stackalloc[] { 1, 2 };")]
    public async Task ReplacedAddedOrImplicitSiteInvalidatesTheReview(string replacement)
    {
        var fingerprint = FingerprintOf(Original);
        var changed = Original.Replace("Span<byte> buffer = stackalloc byte[8];", replacement, StringComparison.Ordinal);
        await Assert.That(Check(changed, fingerprint)).IsNotEmpty();
    }

    [Test]
    public async Task ChangedConsumerInvalidatesTheReview()
    {
        var fingerprint = FingerprintOf(Original);
        var changed = Original.Replace("buffer.Clear();", "", StringComparison.Ordinal);
        await Assert.That(Check(changed, fingerprint).Single()).Contains("enclosing member changed");
        await Assert.That(Check(changed)).IsEmpty();
    }

    [Test]
    public async Task ImplicitSiteIsFound()
    {
        var source = Original.Replace("Span<byte> buffer = stackalloc byte[8];", "Span<int> buffer = stackalloc[] { 1, 2 };", StringComparison.Ordinal);
        await Assert.That(FindSites("src/Example.cs", source.Replace("{0}", "", StringComparison.Ordinal), AuditConfigurations()).Length).IsEqualTo(1);
        await Assert.That(Check(source)).IsEmpty();
    }

    [Test]
    [Arguments("NET10_0_OR_GREATER", 2)]
    [Arguments("DEBUG", 2)]
    [Arguments("RELEASE", 2)]
    public async Task FrameworkAndFlavorBranchesAreAudited(string symbol, int expectedConfigurations)
    {
        var source = Original.Replace("Span<byte> buffer = stackalloc byte[8];",
            $"#if {symbol}\n        Span<byte> buffer = stackalloc byte[8];\n#else\n        Span<byte> buffer = new byte[8];\n#endif", StringComparison.Ordinal);
        var sites = FindSites("src/Example.cs", source.Replace("{0}", "", StringComparison.Ordinal), AuditConfigurations());
        await Assert.That(sites.Single().Configurations.Length).IsEqualTo(expectedConfigurations);
        await Assert.That(Validate("src/Example.cs", source.Replace("{0}", "", StringComparison.Ordinal), AuditConfigurations())).IsNotEmpty();
        await Assert.That(Check(source)).IsEmpty();
    }

    [Test]
    public async Task ChangedInactiveBranchInvalidatesTheReview()
    {
        var source = Original.Replace("Span<byte> buffer = stackalloc byte[8];",
            "#if NET10_0_OR_GREATER\n        Span<byte> buffer = stackalloc byte[8];\n#else\n        Span<byte> buffer = stackalloc byte[8]; buffer.Clear();\n#endif", StringComparison.Ordinal);
        var fingerprint = FingerprintOf(source);
        var changed = source.Replace("buffer.Clear();\n#endif", "\n#endif", StringComparison.Ordinal);
        await Assert.That(Check(changed, fingerprint)).IsNotEmpty();
    }

    [Test]
    public async Task UncompiledBranchFailsClosed()
    {
        var source = Original.Replace("Span<byte> buffer = stackalloc byte[8];",
            "#if NEVER_DEFINED\n        Span<byte> buffer = stackalloc byte[8];\n#endif", StringComparison.Ordinal).Replace("{0}", "", StringComparison.Ordinal);
        await Assert.That(() => FindSites("src/Example.cs", source, AuditConfigurations())).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EachSiteInAStatementNeedsItsOwnJustification()
    {
        var source = Original.Replace("Span<byte> buffer = stackalloc byte[8];",
            "Span<byte> buffer = Flag ? stackalloc byte[8] : stackalloc byte[16];", StringComparison.Ordinal);
        var fingerprint = FingerprintOf(source);
        await Assert.That(Check(source, fingerprint).Single()).Contains("need 2");
        var twice = source.Replace("{0}", string.Format(System.Globalization.CultureInfo.InvariantCulture, Reviewed, fingerprint) + "\n        "
            + string.Format(System.Globalization.CultureInfo.InvariantCulture, Reviewed, fingerprint), StringComparison.Ordinal);
        await Assert.That(Validate("src/Example.cs", twice, AuditConfigurations())).IsEmpty();
    }

    [Test]
    [Arguments("init: x; read: synthetic read boundary control; failure: synthetic failure path control.")]
    [Arguments("init: synthetic initialization control; read: ; failure: synthetic failure path control.")]
    [Arguments("init: synthetic initialization control; read: synthetic read boundary control; failure:")]
    public async Task EachJustificationFieldIsRequired(string fields)
    {
        var fingerprint = FingerprintOf(Original);
        var source = Original.Replace("{0}", $"// stackalloc-audit[{fingerprint}]: {fields}", StringComparison.Ordinal);
        await Assert.That(Validate("src/Example.cs", source, AuditConfigurations()).Single()).Contains("too short");
    }

    [Test]
    public async Task JustificationMustSitDirectlyAboveTheStatement()
    {
        var fingerprint = FingerprintOf(Original);
        var separated = Original.Replace("{0}", string.Format(System.Globalization.CultureInfo.InvariantCulture, Reviewed, fingerprint) + "\n\n", StringComparison.Ordinal);
        await Assert.That(Validate("src/Example.cs", separated, AuditConfigurations())).IsNotEmpty();
        var wrapped = Original.Replace("{0}", $"// stackalloc-audit[{fingerprint}]: init: synthetic initialization control;\n        // read: synthetic read boundary control;\n        // failure: synthetic failure path control.", StringComparison.Ordinal);
        await Assert.That(Validate("src/Example.cs", wrapped, AuditConfigurations())).IsEmpty();
    }

    [Test]
    public async Task CommentsAndStringsAreNotSites()
    {
        const string source = "class Example { string Text = \"stackalloc byte[8]\"; /* stackalloc byte[8] */ // stackalloc\n }";
        await Assert.That(FindSites("src/Example.cs", source, AuditConfigurations())).IsEmpty();
    }

    [Test]
    public async Task CompiledInventoryCountsLocallocAndSkipLocalsInit()
    {
        const string source = """
            using System;
            [module: System.Runtime.CompilerServices.SkipLocalsInit]
            public static class Probe
            {
                public static int One() { Span<byte> a = stackalloc byte[8]; return a[0]; }
                public static int One(int size) { Span<byte> a = stackalloc byte[size]; return a[0]; }
                public static int Two(bool flag) { Span<byte> a = flag ? stackalloc byte[4] : stackalloc byte[8]; Span<long> b = stackalloc long[] { 1L << 40 }; return a[0] + (int)b[0]; }
                public static int None(int x) { switch (x) { case 1: return 2; case 2: return 3; default: return 4; } }
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"local-init-probe-{Guid.NewGuid():N}.dll");
        try
        {
            var compilation = TestInspectionSource.CreateCompilation([CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))])
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, optimizationLevel: OptimizationLevel.Release));
            var result = compilation.Emit(path);
            await Assert.That(result.Success).IsTrue();
            var (sites, targets) = InspectAssembly(path, "net10.0");
            var overloads = sites.Where(site => site.Method.StartsWith("Probe::One``", StringComparison.Ordinal)).ToArray();
            await Assert.That(overloads.Length).IsEqualTo(2);
            await Assert.That(overloads.Select(site => site.Method).Distinct().Count()).IsEqualTo(2);
            await Assert.That(overloads.All(site => site.Count == 1 && !site.InitLocals)).IsTrue();
            await Assert.That(sites.Single(site => site.Method.StartsWith("Probe::Two``", StringComparison.Ordinal)).Count).IsEqualTo(3);
            await Assert.That(targets).IsEquivalentTo(new[] { "module" });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
