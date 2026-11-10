using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

/// <summary>
/// Runtime async ignores <c>[AsyncMethodBuilder]</c>, so a pooling-builder method without
/// <c>[RuntimeAsyncMethodGeneration(false)]</c> silently allocates on every suspension on net11.0.
/// </summary>
public class RuntimeAsyncOptOutTests
{
    [Test]
    public async Task EveryPoolingBuilderMethodOptsOutOfRuntimeAsync()
    {
        var pooled = TestInspectionSource.Library(net11: true).Trees
            .SelectMany(source => source.Tree.GetRoot().DescendantNodes()
                .OfType<AttributeSyntax>()
                .Where(attribute => attribute.Name.ToString() == "AsyncMethodBuilder"
                    && attribute.ArgumentList?.ToString().Contains("Pooling", StringComparison.Ordinal) == true)
                .Select(attribute => (source.Path, Declaration: attribute.Parent!.Parent!)))
            .ToArray();
        // 151 sites when this guard was introduced; a sudden zero means the scan broke.
        await Assert.That(pooled.Length).IsGreaterThan(100);
        var missing = pooled
            .Where(site => !site.Declaration.ChildNodes().OfType<AttributeListSyntax>()
                .SelectMany(list => list.Attributes)
                .Any(attribute => attribute.Name.ToString() == "RuntimeAsyncMethodGeneration"
                    && attribute.ArgumentList?.Arguments.Single().ToString() == "false"))
            .Select(site => $"{site.Path}:{site.Declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException("Pooling builder without [RuntimeAsyncMethodGeneration(false)]:\n" + string.Join('\n', missing));
    }
}
