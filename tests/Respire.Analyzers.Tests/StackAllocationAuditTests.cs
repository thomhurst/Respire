using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Respire.Analyzers.Tests.StackAllocationAudit;
using static Respire.Analyzers.Tests.TestInspectionSource;

namespace Respire.Analyzers.Tests;

public class StackAllocationAuditTests
{
    private const string Original = "class Example { void Read() { Span<byte> buffer = stackalloc byte[8]; buffer.Clear(); Consume(buffer); } }";

    [Test]
    public async Task EveryPackageSiteHasAnIndividualCurrentJustification()
    {
        using var reader = new StreamReader(OpenResource("StackAllocationAudit.md"));
        var reviews = ReadReviews(await reader.ReadToEndAsync());
        await Assert.That(reviews).IsNotEmpty();
        var sources = ReadLibrarySources().Where(source => !source.Path.StartsWith("LibrarySource/Respire.Analyzers/", StringComparison.Ordinal)).ToArray();
        await Assert.That(sources).IsNotEmpty();
        using var configurationsReader = new StreamReader(OpenResource("StackAllocationConfigurations.txt"));
        var configurations = (await configurationsReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('|')).ToArray();
        await Assert.That(configurations).IsNotEmpty();
        var importedSources = typeof(StackAllocationAuditTests).Assembly.GetManifestResourceNames()
            .Select(resource => (Resource: resource, Path: resource.Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("ImportedSource/", StringComparison.Ordinal))
            .Select(source =>
            {
                using var importedReader = new StreamReader(OpenResource(source.Resource));
                var parts = source.Path.Split('/', 4);
                return (Package: parts[1], Framework: parts[2], Path: parts[3], Text: importedReader.ReadToEnd());
            }).ToArray();
        await Assert.That(importedSources).IsNotEmpty();
        foreach (var group in configurations.GroupBy(parts => parts[0] + "|" + parts[1]))
        {
            await Assert.That(group.Any(parts => parts[2].Split(',').Contains("DEBUG"))).IsTrue();
            await Assert.That(group.Any(parts => parts[2].Split(',').Contains("RELEASE"))).IsTrue();
        }
        var sites = configurations.SelectMany(parts =>
        {
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2]))
                throw new InvalidOperationException("Invalid package configuration: " + string.Join('|', parts));
            var packageSources = sources.Where(source => source.Path.StartsWith("LibrarySource/" + parts[0] + "/", StringComparison.Ordinal)
                || source.Path.StartsWith("LibrarySource/Shared/", StringComparison.Ordinal)).ToArray();
            if (packageSources.Length == 0) throw new InvalidOperationException("No embedded source for package " + parts[0]);
            var configuration = new SourceConfiguration(parts[1], parts[2].Split(','));
            return packageSources.SelectMany(source => FindSites("src/" + source.Path["LibrarySource/".Length..], source.Text, configuration))
                .Concat(importedSources.Where(source => source.Package == parts[0] && source.Framework == parts[1])
                    .SelectMany(source => FindSites(source.Path, source.Text, configuration)));
        }).DistinctBy(site => site.Key).ToArray();
        await Assert.That(sites).IsNotEmpty();
        var errors = Validate(sites, reviews);
        if (errors.Length != 0) throw new InvalidOperationException(string.Join('\n', errors));
    }

    [Test]
    [Arguments("class Example { void Read() { Span<byte> buffer = stackalloc byte[8]; buffer.Clear(); Consume(buffer); Span<int> added = stackalloc int[1]; } }")]
    [Arguments("class Example { void Read() { Span<byte> replacement = stackalloc byte[8]; replacement.Clear(); Consume(replacement); } }")]
    [Arguments("class Example { void Read() { Span<byte> buffer = stackalloc byte[8]; Consume(buffer); } }")]
    [Arguments("class Example { void Read() { Span<int> buffer = stackalloc[] { 1, 2 }; Consume(buffer); } }")]
    public async Task AddedReplacedImplicitOrChangedConsumerRequiresReview(string changed)
    {
        var original = Scan(Original, false);
        await Assert.That(Validate(original, Reviewed(original))).IsEmpty();
        await Assert.That(Validate(Scan(changed, false), Reviewed(original))).IsNotEmpty();
    }

    [Test]
    public async Task ImplicitSiteHasItsOwnAudit()
    {
        var sites = Scan("class Example { void Read() { Span<int> buffer = stackalloc[] { 1, 2 }; } }", false);
        await Assert.That(sites.Length).IsEqualTo(1);
        await Assert.That(Validate(sites, [])).IsNotEmpty();
        await Assert.That(Validate(sites, Reviewed(sites))).IsEmpty();
    }

    [Test]
    public async Task ImportedExtensionBlockAllocationIsRecognized()
    {
        const string source = "class Example { extension(Guid) { public static int Read() { Span<int> buffer = stackalloc[] { 1 }; return buffer[0]; } } }";
        var sites = Scan(source, false);
        await Assert.That(sites.Length).IsEqualTo(1);
        await Assert.That(Validate(sites, [])).IsNotEmpty();
        await Assert.That(Validate(sites, Reviewed(sites))).IsEmpty();
    }

    [Test]
    public async Task FrameworkBranchesRequireSeparateReviews()
    {
        const string source = """
            class Example { void Read() {
            #if NET9_0_OR_GREATER
                Span<int> modern = stackalloc[] { 1, 2 };
            #else
                Span<byte> older = stackalloc byte[8];
            #endif
            } }
            """;
        var older = Scan(source, false);
        var modern = Scan(source, true);
        await Assert.That(older.Length).IsEqualTo(1);
        await Assert.That(modern.Length).IsEqualTo(1);
        await Assert.That(older[0].Fingerprint == modern[0].Fingerprint).IsFalse();
        await Assert.That(Validate(older.Concat(modern), Reviewed(older))).IsNotEmpty();
        await Assert.That(Validate(older.Concat(modern), Reviewed(older.Concat(modern)))).IsEmpty();
    }

    [Test]
    public async Task DebugOnlyAllocationRequiresReview()
    {
        const string source = """
            class Example { void Read() {
            #if DEBUG
                Span<int> debug = stackalloc[] { 1, 2 };
            #endif
            } }
            """;
        var configuration = ReadSourceConfigurations().First();
        var release = FindSites("src/Example.cs", source, configuration with { Symbols = configuration.Symbols.Where(symbol => symbol != "DEBUG").ToArray() });
        var debug = FindSites("src/Example.cs", source, configuration with { Symbols = configuration.Symbols.Append("DEBUG").Distinct().ToArray() });
        await Assert.That(release).IsEmpty();
        await Assert.That(debug.Length).IsEqualTo(1);
        await Assert.That(Validate(debug, Reviewed(release))).IsNotEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task EachJustificationFieldIsRequired(int missingField)
    {
        var sites = Scan(Original, false);
        var review = Reviewed(sites).Single();
        review = missingField switch
        {
            0 => review with { Initialization = "" },
            1 => review with { ReadBoundary = " " },
            _ => review with { FailurePath = "" },
        };
        await Assert.That(Validate(sites, [review]).Single()).IsEqualTo("Missing justification: " + review.Key);
    }

    [Test]
    public async Task DuplicateAndDeletedAuditRowsFail()
    {
        var sites = Scan(Original, false);
        var review = Reviewed(sites).Single();
        await Assert.That(Validate(sites, [review, review])).IsNotEmpty();
        await Assert.That(Validate(sites, [])).IsNotEmpty();
        await Assert.That(Validate([], [review])).IsNotEmpty();
    }

    [Test]
    public async Task TableParserPreservesIndividualJustificationsAndBothFrameworks()
    {
        var rows = ReadReviews("| net8.0,net10.0 | `src/Example.cs` | 1 | ABC | Read/buffer | Clear all eight bytes. | Consume eight bytes. | Throw before read. |");
        await Assert.That(rows.Length).IsEqualTo(2);
        await Assert.That(rows[0].Key).IsEqualTo("net8.0|src/Example.cs|1|ABC");
        await Assert.That(rows[1].Key).IsEqualTo("net10.0|src/Example.cs|1|ABC");
        await Assert.That(rows[0].Initialization).IsEqualTo("Clear all eight bytes.");
        await Assert.That(rows[0].ReadBoundary).IsEqualTo("Consume eight bytes.");
        await Assert.That(rows[0].FailurePath).IsEqualTo("Throw before read.");
    }

    private static Site[] Scan(string source, bool net10)
        => FindSites("src/Example.cs", source, ReadSourceConfigurations().Single(configuration => configuration.Framework == (net10 ? "net10.0" : "net8.0")));

    private static IEnumerable<Review> Reviewed(IEnumerable<Site> sites)
        => sites.Select(site => new Review(site.Key, "Synthetic initialization control.", "Synthetic bounded read control.", "Synthetic failure control."));
}
