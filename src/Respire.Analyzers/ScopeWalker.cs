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
internal static partial class ScopeWalker
{
    internal enum BarrierStartPolicy { Exclude, Include }

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

    /// <summary>True when the expression is assigned directly to the discard identifier.</summary>
    public static bool IsDiscarded(ExpressionSyntax expression)
        => expression.FirstAncestorOrSelf<AssignmentExpressionSyntax>() is { } assignment
           && assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" }
           && IsSame(Unwrap(assignment.Right), expression);

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
        if (graph is null)
        {
            return IsUnconditionalTopLevelSequence(scope, before, after, requireExitCoverage: false);
        }

        if (FindBlock(graph, before) is null
            || FindBlock(graph, after) is not { } afterBlock)
        {
            return false;
        }

        var systemException = GetSystemException(semanticModel);
        return PathExistsAvoiding(graph, systemException, graph.Blocks[0], int.MinValue, afterBlock, after.SpanStart, [], cancellationToken: cancellationToken)
               && !PathExistsAvoiding(graph, systemException, graph.Blocks[0], int.MinValue, afterBlock, after.SpanStart, [before], cancellationToken: cancellationToken);
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
        if (graph is null)
        {
            return IsUnconditionalTopLevelSequence(scope, before, after, requireExitCoverage: true);
        }

        if (FindBlock(graph, before) is not { } beforeBlock
            || FindBlock(graph, after) is null)
        {
            return false;
        }

        return !PathExistsAvoiding(graph, GetSystemException(semanticModel), beforeBlock, before.SpanStart,
            graph.Blocks[graph.Blocks.Length - 1], int.MaxValue, [after], cancellationToken: cancellationToken);
    }

    /// <summary>True when control can flow from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static bool CanReach(
        SemanticModel semanticModel,
        SyntaxNode scope,
        SyntaxNode before,
        SyntaxNode after,
        CancellationToken cancellationToken)
    {
        var graph = CreateControlFlowGraph(semanticModel, scope, cancellationToken);
        if (graph is null)
        {
            return IsUnconditionalTopLevelSequence(scope, before, after, requireExitCoverage: false);
        }

        if (FindBlock(graph, before) is not { } beforeBlock
            || FindBlock(graph, after) is not { } afterBlock)
        {
            return false;
        }

        return PathExistsAvoiding(
            graph, GetSystemException(semanticModel),
            beforeBlock, before.SpanStart, afterBlock, after.SpanStart, [], cancellationToken: cancellationToken);
    }

    /// <summary>
    /// True when control can flow from <paramref name="before"/> to <paramref name="after"/>
    /// without crossing any node in <paramref name="barriers"/>.
    /// </summary>
    public static bool CanReachWithoutCrossing(
        SemanticModel semanticModel,
        SyntaxNode scope,
        SyntaxNode before,
        SyntaxNode after,
        IEnumerable<SyntaxNode> barriers,
        CancellationToken cancellationToken,
        BarrierStartPolicy startPolicy = BarrierStartPolicy.Exclude)
    {
        var graph = CreateControlFlowGraph(semanticModel, scope, cancellationToken);
        if (graph is null)
        {
            return IsUnconditionalTopLevelSequence(scope, before, after, requireExitCoverage: false)
                   && !barriers.Any(barrier => barrier.SpanStart > before.SpanStart
                                               && barrier.SpanStart < after.SpanStart);
        }

        if (FindBlock(graph, before) is not { } beforeBlock
            || FindBlock(graph, after) is not { } afterBlock)
        {
            return false;
        }

        return PathExistsAvoiding(
            graph, GetSystemException(semanticModel),
            beforeBlock, before.SpanStart, afterBlock, after.SpanStart, barriers, startPolicy, cancellationToken);
    }

    /// <summary>True when every path to <paramref name="after"/> crosses one of <paramref name="barriers"/>.</summary>
    public static bool CollectivelyDominates(
        SemanticModel semanticModel,
        SyntaxNode scope,
        IEnumerable<SyntaxNode> barriers,
        SyntaxNode after,
        CancellationToken cancellationToken)
    {
        var barrierArray = barriers.ToArray();
        if (barrierArray.Length == 0)
        {
            return false;
        }

        var graph = CreateControlFlowGraph(semanticModel, scope, cancellationToken);
        if (graph is null)
        {
            return barrierArray.Any(barrier => Dominates(
                semanticModel, scope, barrier, after, cancellationToken));
        }

        if (FindBlock(graph, after) is not { } afterBlock)
        {
            return false;
        }

        return !PathExistsAvoiding(
            graph, GetSystemException(semanticModel),
            graph.Blocks[0], int.MinValue, afterBlock, after.SpanStart, barrierArray, cancellationToken: cancellationToken);
    }

    /// <summary>True when every path from <paramref name="before"/> to exit crosses one of <paramref name="barriers"/>.</summary>
    public static bool CollectivelyPostDominates(
        SemanticModel semanticModel,
        SyntaxNode scope,
        SyntaxNode before,
        IEnumerable<SyntaxNode> barriers,
        CancellationToken cancellationToken)
    {
        var barrierArray = barriers.ToArray();
        if (barrierArray.Length == 0)
        {
            return false;
        }

        var graph = CreateControlFlowGraph(semanticModel, scope, cancellationToken);
        if (graph is null)
        {
            return barrierArray.Any(barrier => PostDominates(
                semanticModel, scope, before, barrier, cancellationToken));
        }

        if (FindBlock(graph, before) is not { } beforeBlock)
        {
            return false;
        }

        return !PathExistsAvoiding(
            graph,
            GetSystemException(semanticModel),
            beforeBlock,
            before.SpanStart,
            graph.Blocks[graph.Blocks.Length - 1],
            int.MaxValue,
            barrierArray, cancellationToken: cancellationToken);
    }

    private static INamedTypeSymbol? GetSystemException(SemanticModel semanticModel)
        => semanticModel.Compilation.GetTypeByMetadataName("System.Exception");

    private static bool PathExistsAvoiding(
        ControlFlowGraph graph,
        INamedTypeSymbol? systemException,
        BasicBlock startBlock,
        int startPosition,
        BasicBlock targetBlock,
        int targetPosition,
        IEnumerable<SyntaxNode> barriers,
        BarrierStartPolicy startPolicy = BarrierStartPolicy.Exclude,
        CancellationToken cancellationToken = default)
        => new ReachabilityWalker(graph, systemException, startBlock, startPosition, targetBlock,
            targetPosition, barriers, startPolicy, cancellationToken).Search();

    private static bool IsUnconditionalTopLevelSequence(
        SyntaxNode scope, SyntaxNode before, SyntaxNode after, bool requireExitCoverage)
    {
        if (scope is not CompilationUnitSyntax
            || before.FirstAncestorOrSelf<GlobalStatementSyntax>() is not { } beforeGlobal
            || after.FirstAncestorOrSelf<GlobalStatementSyntax>() is not { } afterGlobal
            || before.FirstAncestorOrSelf<StatementSyntax>() is not { } beforeStatement
            || after.FirstAncestorOrSelf<StatementSyntax>() is not { } afterStatement
            || !IsSame(beforeStatement, beforeGlobal.Statement)
            || !IsSame(afterStatement, afterGlobal.Statement))
        {
            return false;
        }

        if (beforeGlobal.SpanStart >= afterGlobal.SpanStart)
        {
            return false;
        }

        return !requireExitCoverage
               || !((CompilationUnitSyntax)scope).Members.OfType<GlobalStatementSyntax>()
                   .Where(statement => statement.SpanStart > beforeGlobal.SpanStart
                                       && statement.SpanStart < afterGlobal.SpanStart)
                   .Any(statement => statement.DescendantNodesAndSelf().Any(static node =>
                       node is ReturnStatementSyntax or ThrowStatementSyntax));
    }

    private static ControlFlowGraph? CreateControlFlowGraph(
        SemanticModel semanticModel, SyntaxNode scope, CancellationToken cancellationToken)
    {
        try
        {
            if (scope is LocalFunctionStatementSyntax localFunction
                && semanticModel.GetDeclaredSymbol(localFunction, cancellationToken) is IMethodSymbol localFunctionSymbol
                && GetEnclosingScope(localFunction) is { } parentScope
                && CreateControlFlowGraph(semanticModel, parentScope, cancellationToken) is { } parentGraph)
            {
                return parentGraph.GetLocalFunctionControlFlowGraph(localFunctionSymbol, cancellationToken);
            }

            if (scope is AnonymousFunctionExpressionSyntax anonymousFunction)
            {
                if (GetEnclosingScope(anonymousFunction) is { } containingScope
                    && CreateControlFlowGraph(semanticModel, containingScope, cancellationToken) is { } containingGraph
                    && FindAnonymousFunction(containingGraph, anonymousFunction) is { } flowAnonymousFunction)
                {
                    return containingGraph.GetAnonymousFunctionControlFlowGraph(
                        flowAnonymousFunction, cancellationToken);
                }

                if (semanticModel.GetOperation(anonymousFunction, cancellationToken)
                        is IAnonymousFunctionOperation operation
                    && CreateRootControlFlowGraph(operation, cancellationToken) is { } rootGraph
                    && FindAnonymousFunction(rootGraph, anonymousFunction) is { } rootAnonymousFunction)
                {
                    return rootGraph.GetAnonymousFunctionControlFlowGraph(
                        rootAnonymousFunction, cancellationToken);
                }
            }

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

    private static ControlFlowGraph? CreateRootControlFlowGraph(
        IOperation operation, CancellationToken cancellationToken)
    {
        while (operation.Parent is { } parent)
        {
            operation = parent;
        }

        return operation switch
        {
            IBlockOperation block => ControlFlowGraph.Create(block, cancellationToken),
            IMethodBodyOperation method => ControlFlowGraph.Create(method, cancellationToken),
            IConstructorBodyOperation constructor => ControlFlowGraph.Create(constructor, cancellationToken),
            IFieldInitializerOperation field => ControlFlowGraph.Create(field, cancellationToken),
            IPropertyInitializerOperation property => ControlFlowGraph.Create(property, cancellationToken),
            IParameterInitializerOperation parameter => ControlFlowGraph.Create(parameter, cancellationToken),
            IAttributeOperation attribute => ControlFlowGraph.Create(attribute, cancellationToken),
            _ => null,
        };
    }

    private static IFlowAnonymousFunctionOperation? FindAnonymousFunction(
        ControlFlowGraph graph, AnonymousFunctionExpressionSyntax syntax)
    {
        foreach (var block in graph.Blocks)
        {
            foreach (var operation in block.Operations)
            {
                if (FindAnonymousFunction(operation, syntax) is { } match)
                {
                    return match;
                }
            }

            if (block.BranchValue is { } branchValue
                && FindAnonymousFunction(branchValue, syntax) is { } branchMatch)
            {
                return branchMatch;
            }
        }

        return null;
    }

    private static IFlowAnonymousFunctionOperation? FindAnonymousFunction(
        IOperation operation, AnonymousFunctionExpressionSyntax syntax)
    {
        if (operation is IFlowAnonymousFunctionOperation anonymousFunction
            && IsSame(anonymousFunction.Syntax, syntax))
        {
            return anonymousFunction;
        }

        foreach (var child in operation.ChildOperations)
        {
            if (FindAnonymousFunction(child, syntax) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static BasicBlock? FindBlock(ControlFlowGraph graph, SyntaxNode node)
        => graph.Blocks.FirstOrDefault(block =>
            block.Operations.Any(operation => operation.Syntax.FullSpan.IntersectsWith(node.Span))
            || block.BranchValue is { } value && value.Syntax.FullSpan.IntersectsWith(node.Span)
            // Roslyn attaches the whole arm to its pattern test. The selected expression
            // executes in a later block and must inherit the successful pattern constraint.
            && !(value is IIsPatternOperation && value.Syntax is SwitchExpressionArmSyntax arm
                 && arm.Expression.Span.Contains(node.Span)));

    /// <summary>The receiver of <c>receiver.Name(...)</c> / <c>receiver.Name</c>, or null.</summary>
    public static ExpressionSyntax? GetReceiver(ExpressionSyntax expression)
        => Unwrap(expression) is MemberAccessExpressionSyntax member && member.IsKind(SyntaxKind.SimpleMemberAccessExpression)
            ? member.Expression
            : null;

    /// <summary>Strips parentheses and null-forgiving operators around an expression.</summary>
    public static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    break;
                case PostfixUnaryExpressionSyntax suppression
                    when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = suppression.Operand;
                    break;
                default:
                    return expression;
            }
        }
    }

    /// <summary>Expands from an expression through enclosing parentheses and null-forgiving operators.</summary>
    public static ExpressionSyntax GetOutermostTransparentExpression(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression.Parent)
            {
                case ParenthesizedExpressionSyntax parenthesized
                    when IsSame(parenthesized.Expression, expression):
                    expression = parenthesized;
                    break;
                case PostfixUnaryExpressionSyntax suppression
                    when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression)
                         && IsSame(suppression.Operand, expression):
                    expression = suppression;
                    break;
                default:
                    return expression;
            }
        }
    }
}
