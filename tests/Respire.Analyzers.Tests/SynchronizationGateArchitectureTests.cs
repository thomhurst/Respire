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
                private readonly object _releaseSync = new();
                protected readonly System.Object _ownershipSync = new();
                public static readonly global::System.Object _disposeLock = new();
                internal readonly object Gate = new();
                internal object NodeStateGate => Gate;
                internal object SharedReadLock { get; } = new();
                private readonly System.Threading.Lock _typedGate = new();
                private readonly object _identity = new();
                private object? _nearestGate;
                private const string Text = "private readonly object _textGate;";
                // private readonly object _commentGate;
            }
            """;
        await Assert.That(FindObjectGates(source)).IsEquivalentTo([
            "_newGate", "_nullableGate", "_releaseSync", "_ownershipSync", "_disposeLock",
            "Gate", "NodeStateGate", "SharedReadLock"]);
    }

    private static string[] FindObjectGates(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var fields = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                && IsObjectType(field.Declaration.Type))
            .SelectMany(field => field.Declaration.Variables)
            .Select(variable => variable.Identifier.ValueText);
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(property => IsObjectType(property.Type))
            .Select(property => property.Identifier.ValueText);
        return fields.Concat(properties).Where(IsGateName).ToArray();
    }

    private static bool IsGateName(string name)
        => name.EndsWith("Gate", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Lock", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Sync", StringComparison.OrdinalIgnoreCase);

    [Test]
    public async Task ContainerFixtureObjectAliasUsesOnlyLockStatementsOnNet8()
    {
        var assembly = typeof(SynchronizationGateArchitectureTests).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.Replace('\\', '/')
            == "LibrarySource/Respire.Testing.Containers/RespireContainerFixture.cs");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var source = await reader.ReadToEndAsync();
        var net8 = CSharpSyntaxTree.ParseText(source).GetRoot();
        var net9 = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(preprocessorSymbols: ["NET9_0_OR_GREATER"])).GetRoot();
        var alias = net8.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Single(usingDirective => usingDirective.Alias?.Name.Identifier.ValueText == "Lock");
        await Assert.That(alias.Name!.ToString()).IsEqualTo("System.Object");
        await Assert.That(net9.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(usingDirective => usingDirective.Alias?.Name.Identifier.ValueText == "Lock")).IsFalse();
        await Assert.That(net8.DescendantNodes().OfType<LockStatementSyntax>()
            .Any(statement => statement.Expression is IdentifierNameSyntax { Identifier.ValueText: "_disposeGate" }))
            .IsTrue();
        await Assert.That(FindDisposeGateMemberUses(net8)).IsEmpty();
    }

    [Test]
    public async Task ContainerAliasGuardDetectsNativeLockMemberCalls()
    {
        const string source = """
            class Example
            {
                void Dispose()
                {
                    using var scope = _disposeGate.EnterScope();
                    // _disposeGate.Exit();
                    var text = "_disposeGate.TryEnter()";
            #if NET9_0_OR_GREATER
                    _disposeGate.Enter();
            #endif
                }
            }
            """;
        await Assert.That(FindDisposeGateMemberUses(CSharpSyntaxTree.ParseText(source).GetRoot()))
            .IsEquivalentTo(["EnterScope"]);
    }

    private static string[] FindDisposeGateMemberUses(SyntaxNode root)
        => root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(member => member.Expression is IdentifierNameSyntax { Identifier.ValueText: "_disposeGate" })
            .Select(member => member.Name.Identifier.ValueText).ToArray();

    private static bool IsObjectType(TypeSyntax type)
        => type is NullableTypeSyntax nullable
            ? IsObjectType(nullable.ElementType)
            : type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword)
                || type.ToString() is "System.Object" or "global::System.Object";
}
