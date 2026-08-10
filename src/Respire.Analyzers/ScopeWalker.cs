using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Respire.Analyzers;

/// <summary>
/// Syntax helpers shared by the rules. Both rules are deliberately intra-scope: they reason about
/// one method body (or one lambda body) and stay silent the moment a value crosses that boundary,
/// because ownership is then someone else's to prove.
/// </summary>
internal static class ScopeWalker
{
    /// <summary>
    /// The executable scope owning <paramref name="node"/> — the method, accessor, local function
    /// or lambda body it lives in. Top-level statements report the whole compilation unit so that
    /// statements can see each other.
    /// </summary>
    public static SyntaxNode? GetEnclosingScope(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case BaseMethodDeclarationSyntax:
                case AccessorDeclarationSyntax:
                    return current;
                case GlobalStatementSyntax:
                    return current.Parent;
            }
        }

        return null;
    }

    /// <summary>All references to <paramref name="symbol"/> written inside <paramref name="scope"/>.</summary>
    public static IEnumerable<IdentifierNameSyntax> FindReferences(
        SyntaxNode scope, ISymbol symbol, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (identifier.Identifier.ValueText != symbol.Name)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol, symbol))
            {
                yield return identifier;
            }
        }
    }

    /// <summary>
    /// True when the node sits in a lambda or local function nested inside <paramref name="scope"/>:
    /// the value may then be used at a time this rule cannot see, so the caller should stay silent.
    /// </summary>
    public static bool IsNestedInLambda(SyntaxNode node, SyntaxNode scope)
    {
        for (var current = node.Parent; current is not null && !IsSame(current, scope); current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Identity for two nodes of the same syntax tree.</summary>
    public static bool IsSame(SyntaxNode left, SyntaxNode right)
        => left.RawKind == right.RawKind && left.FullSpan == right.FullSpan;

    /// <summary>The receiver of <c>receiver.Name(...)</c> / <c>receiver.Name</c>, or null.</summary>
    public static ExpressionSyntax? GetReceiver(ExpressionSyntax expression)
        => Unwrap(expression) is MemberAccessExpressionSyntax member && member.IsKind(SyntaxKind.SimpleMemberAccessExpression)
            ? member.Expression
            : null;

    /// <summary>Strips parentheses so <c>(x)</c> analyses the same as <c>x</c>.</summary>
    public static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }
}
