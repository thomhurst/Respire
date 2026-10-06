using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class SynchronizationGateArchitectureTests
{
    [Test]
    public async Task LibraryReadonlyGatesUseLockRatherThanObject()
    {
        var assembly = typeof(SynchronizationGateArchitectureTests).Assembly;
        var sources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("LibrarySource/", StringComparison.Ordinal)).ToArray();
        await Assert.That(sources).IsNotEmpty();
        var violations = new List<string>();
        foreach (var source in sources)
        {
            using var stream = assembly.GetManifestResourceStream(source)!;
            using var reader = new StreamReader(stream);
            foreach (var gate in FindObjectGates(await reader.ReadToEndAsync()))
                violations.Add($"{source}: {gate}");
        }
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task DetectionDistinguishesSynchronizationGatesFromOtherObjectUses()
    {
        const string source = """
            class Example
            {
                private readonly object _newGate = new();
                private readonly object? _nullableGate;
                private readonly System.Threading.Lock _typedGate = new();
                private readonly object _identity = new();
                private object? _nearestGate;
                private const string Text = "private readonly object _textGate;";
                // private readonly object _commentGate;
            }
            """;
        await Assert.That(FindObjectGates(source)).IsEquivalentTo(["_newGate", "_nullableGate"]);
    }

    private static string[] FindObjectGates(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Modifiers.Any(SyntaxKind.PrivateKeyword)
                && field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                && IsObjectType(field.Declaration.Type))
            .SelectMany(field => field.Declaration.Variables)
            .Select(variable => variable.Identifier.ValueText)
            .Where(name => name.StartsWith('_') && name.EndsWith("Gate", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool IsObjectType(TypeSyntax type)
        => type is NullableTypeSyntax nullable
            ? IsObjectType(nullable.ElementType)
            : type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword);
}
