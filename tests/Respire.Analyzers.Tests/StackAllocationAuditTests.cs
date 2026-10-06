using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class StackAllocationAuditTests
{
    [Test]
    public async Task PackageStackAllocationsMatchReviewedInventory()
    {
        var assembly = typeof(StackAllocationAuditTests).Assembly;
        using var auditStream = assembly.GetManifestResourceStream("StackAllocationAudit")!;
        using var auditReader = new StreamReader(auditStream);
        var inventory = Regex.Matches(await auditReader.ReadToEndAsync(),
                @"(?m)^\| `(?<path>src/[^`]+\.cs)` \| (?<count>\d+) \|\r?$")
            .ToDictionary(match => match.Groups["path"].Value,
                match => int.Parse(match.Groups["count"].Value));
        await Assert.That(inventory).IsNotEmpty();

        var actual = new Dictionary<string, int>();
        var sources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("LibrarySource/", StringComparison.Ordinal)
                && !name.StartsWith("LibrarySource/Respire.Analyzers/", StringComparison.Ordinal)).ToArray();
        await Assert.That(sources).IsNotEmpty();
        foreach (var resource in sources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var count = CountSites(await reader.ReadToEndAsync());
            if (count != 0) actual.Add("src/" + resource["LibrarySource/".Length..].Replace('\\', '/'), count);
        }
        await Assert.That(actual.OrderBy(pair => pair.Key).ToArray())
            .IsEquivalentTo(inventory.OrderBy(pair => pair.Key).ToArray());
    }

    [Test]
    public async Task InventoryDetectsExplicitImplicitAndFrameworkSpecificSites()
    {
        const string source = """
            class Example
            {
                void Read()
                {
                    var explicitBuffer = stackalloc byte[16];
                    var implicitBuffer = stackalloc[] { 1, 2 };
            #if NET9_0_OR_GREATER
                    var modernBuffer = stackalloc char[8];
            #else
                    var olderBuffer = stackalloc char[4];
            #endif
                    var text = "stackalloc byte[99]";
                    // stackalloc byte[99]
                }
            }
            """;
        await Assert.That(CountSites(source)).IsEqualTo(4);
    }

    private static int CountSites(string source)
    {
        string[][] frameworks = [
            ["NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER"],
            ["NET", "NETCOREAPP", "NET10_0", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER"]];
        var positions = new HashSet<int>();
        foreach (var symbols in frameworks)
        {
            var root = CSharpSyntaxTree.ParseText(source,
                new CSharpParseOptions(preprocessorSymbols: symbols)).GetRoot();
            foreach (var node in root.DescendantNodes())
                if (node.Kind() is SyntaxKind.StackAllocArrayCreationExpression or SyntaxKind.ImplicitStackAllocArrayCreationExpression)
                    positions.Add(node.SpanStart);
        }
        return positions.Count;
    }
}
