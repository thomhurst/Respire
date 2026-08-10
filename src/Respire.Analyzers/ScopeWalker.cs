using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

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

    /// <summary>True when <paramref name="node"/> is used only to produce a compile-time name.</summary>
    public static bool IsInsideNameOf(
        SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
    {
        for (var operation = semanticModel.GetOperation(node, cancellationToken);
             operation is not null;
             operation = operation.Parent)
        {
            if (operation is INameOfOperation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Identity for two nodes of the same syntax tree.</summary>
    public static bool IsSame(SyntaxNode left, SyntaxNode right)
        => left.RawKind == right.RawKind && left.FullSpan == right.FullSpan;

    /// <summary>True when every control-flow path to <paramref name="after"/> crosses <paramref name="before"/>.</summary>
    public static bool Dominates(
        SemanticModel semanticModel,
        SyntaxNode scope,
        SyntaxNode before,
        SyntaxNode after,
        CancellationToken cancellationToken)
    {
        var graph = CreateControlFlowGraph(semanticModel, scope, cancellationToken);
        if (graph is null
            || FindBlock(graph, before) is not { } beforeBlock
            || FindBlock(graph, after) is not { } afterBlock)
        {
            return false;
        }

        if (beforeBlock.Ordinal == afterBlock.Ordinal)
        {
            return before.SpanStart < after.SpanStart;
        }

        return ComputeDominators(graph, reverse: false)[afterBlock.Ordinal].Contains(beforeBlock.Ordinal);
    }

    /// <summary>True when every control-flow path from <paramref name="before"/> to exit crosses <paramref name="after"/>.</summary>
    public static bool PostDominates(
        SemanticModel semanticModel,
        SyntaxNode scope,
        SyntaxNode before,
        SyntaxNode after,
        CancellationToken cancellationToken)
    {
        var graph = CreateControlFlowGraph(semanticModel, scope, cancellationToken);
        if (graph is null
            || FindBlock(graph, before) is not { } beforeBlock
            || FindBlock(graph, after) is not { } afterBlock)
        {
            return false;
        }

        if (beforeBlock.Ordinal == afterBlock.Ordinal)
        {
            return before.SpanStart < after.SpanStart;
        }

        return ComputeDominators(graph, reverse: true)[beforeBlock.Ordinal].Contains(afterBlock.Ordinal);
    }

    private static ControlFlowGraph? CreateControlFlowGraph(
        SemanticModel semanticModel, SyntaxNode scope, CancellationToken cancellationToken)
    {
        try
        {
            return ControlFlowGraph.Create(scope, semanticModel, cancellationToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static BasicBlock? FindBlock(ControlFlowGraph graph, SyntaxNode node)
        => graph.Blocks.FirstOrDefault(block =>
            block.Operations.Any(operation => operation.Syntax.FullSpan.IntersectsWith(node.Span))
            || block.BranchValue?.Syntax.FullSpan.IntersectsWith(node.Span) == true);

    private static HashSet<int>[] ComputeDominators(ControlFlowGraph graph, bool reverse)
    {
        var blocks = graph.Blocks;
        var all = new HashSet<int>(
            blocks.Where(static block => block.IsReachable).Select(static block => block.Ordinal));
        var root = reverse ? blocks[blocks.Length - 1].Ordinal : blocks[0].Ordinal;
        var dominators = new HashSet<int>[blocks.Length];

        foreach (var block in blocks)
        {
            dominators[block.Ordinal] = block.Ordinal == root ? [root] : new HashSet<int>(all);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var block in blocks)
            {
                if (!block.IsReachable || block.Ordinal == root)
                {
                    continue;
                }

                var adjacent = reverse ? GetSuccessors(block) : block.Predecessors.Select(static branch => branch.Source);
                var sets = adjacent.Where(static candidate => candidate.IsReachable)
                    .Select(candidate => dominators[candidate.Ordinal]).ToArray();
                var next = sets.Length == 0 ? [] : new HashSet<int>(sets[0]);
                foreach (var set in sets.Skip(1))
                {
                    next.IntersectWith(set);
                }

                next.Add(block.Ordinal);
                if (!dominators[block.Ordinal].SetEquals(next))
                {
                    dominators[block.Ordinal] = next;
                    changed = true;
                }
            }
        }
        while (changed);

        return dominators;
    }

    private static IEnumerable<BasicBlock> GetSuccessors(BasicBlock block)
    {
        var fallThrough = block.FallThroughSuccessor?.Destination;
        if (fallThrough is not null)
        {
            yield return fallThrough;
        }

        if (block.ConditionalSuccessor?.Destination is { } conditional
            && conditional.Ordinal != fallThrough?.Ordinal)
        {
            yield return conditional;
        }
    }

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
