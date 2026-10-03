using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

// Shared exception-type evidence for disposal and flush analysis. The graph walker
// uses this evidence when selecting catch regions.
internal static class ScopeExitAnalysis
{
    private static readonly string[] SimpleExceptionTypes =
        ["System.Exception", "System.InvalidOperationException", "System.ArgumentException"];
    private static readonly ConditionalWeakTable<Compilation, INamedTypeSymbol?[]> SimpleExceptionSymbols = new();

    /// <summary>
    /// The exact runtime type raised by throwing <paramref name="operation"/>, or null when it
    /// is unknown: NullReferenceException for a compile-time null, the type of a simple fresh
    /// construction, or the type a never-reassigned local was initialized with.
    /// </summary>
    internal static ITypeSymbol? GetExactThrownType(SemanticModel semanticModel, IOperation? operation)
    {
        if (operation?.ConstantValue is { HasValue: true, Value: null })
        {
            return semanticModel.Compilation.GetTypeByMetadataName("System.NullReferenceException");
        }

        return GetKnownExactExceptionType(semanticModel.Compilation, operation)
               ?? GetUnreassignedCreationType(semanticModel, operation);
    }

    /// <summary>
    /// The created type of a local initialized with <c>new X(...)</c> and never written again,
    /// so every read is that exact non-null instance. A construction exception happens at the
    /// initializer, before any read. Any possible write or by-reference use returns null.
    /// </summary>
    private static ITypeSymbol? GetUnreassignedCreationType(SemanticModel semanticModel, IOperation? operation)
    {
        if (operation is not ILocalReferenceOperation { Local: { IsRef: false, IsConst: false } local }
            || local.DeclaringSyntaxReferences.Length != 1
            || local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } value } declarator
            || declarator.SyntaxTree != semanticModel.SyntaxTree
            || semanticModel.GetOperation(ScopeWalker.Unwrap(value)) is not IObjectCreationOperation { Type: { } createdType }
            // A switch section's locals are scoped to the whole switch block.
            || declarator.Ancestors().FirstOrDefault(static ancestor =>
                ancestor is BlockSyntax or SwitchStatementSyntax or CompilationUnitSyntax) is not { } scope)
        {
            return null;
        }

        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText == local.Name
                && SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(identifier).Symbol, local)
                && IsPotentialWrite(identifier))
            {
                return null;
            }
        }

        return createdType;
    }

    private static bool IsPotentialWrite(IdentifierNameSyntax identifier)
    {
        // Assignment and deconstruction targets, including member writes, are rejected conservatively.
        if (identifier.Ancestors().Any(ancestor =>
                ancestor is AssignmentExpressionSyntax assignment && assignment.Left.Span.Contains(identifier.Span)
                || ancestor is ForEachVariableStatementSyntax loop && loop.Variable.Span.Contains(identifier.Span)))
        {
            return true;
        }

        SyntaxNode node = identifier;
        while (node.Parent is ParenthesizedExpressionSyntax
               || node.Parent is PostfixUnaryExpressionSyntax suppression
               && suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression))
        {
            node = node.Parent;
        }

        return node.Parent switch
        {
            ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None),
            RefExpressionSyntax or MakeRefExpressionSyntax => true,
            PrefixUnaryExpressionSyntax prefix => prefix.Kind() is SyntaxKind.PreIncrementExpression
                or SyntaxKind.PreDecrementExpression or SyntaxKind.AddressOfExpression,
            PostfixUnaryExpressionSyntax postfix => postfix.Kind() is SyntaxKind.PostIncrementExpression
                or SyntaxKind.PostDecrementExpression,
            _ => false,
        };
    }

    /// <summary>
    /// The exact type of a fresh exception whose construction cannot itself raise a different
    /// exception, or null. Shared by the syntactic exit analysis and the flow-graph walker.
    /// </summary>
    internal static ITypeSymbol? GetKnownExactExceptionType(Compilation compilation, IOperation? operation)
    {
        // User constructors (including new T()) and argument evaluation can throw a
        // different exception before the explicit throw. Keep their catches possible.
        if (operation is not IObjectCreationOperation creation
            || creation.Initializer is not null
            || creation.Arguments.Any(argument => !argument.Value.ConstantValue.HasValue
                && argument.Value is not ILocalReferenceOperation and not IParameterReferenceOperation))
            return null;

        // These framework constructors only store the supplied message/inner exception.
        // Other constructors remain opaque; this is not an interprocedural exception proof.
        var simpleTypes = SimpleExceptionSymbols.GetValue(compilation, static candidate =>
            SimpleExceptionTypes.Select(candidate.GetTypeByMetadataName).ToArray());
        return simpleTypes.Any(type => SymbolEqualityComparer.Default.Equals(creation.Type, type))
            ? creation.Type
            : null;
    }

}