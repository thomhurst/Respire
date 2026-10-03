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
    internal enum BarrierStartPolicy { Exclude, Include }
    internal enum ExitMode { Disposal, FlushProof }
    private enum CatchMatch { None, Possible, Guaranteed }
    private static readonly string[] SimpleExceptionTypes =
        ["System.Exception", "System.InvalidOperationException", "System.ArgumentException"];

    /// <summary>Whether an explicit exit can skip the statement following this one.</summary>
    public static bool CanBypassFollowingStatement(
        SemanticModel semanticModel, StatementSyntax statement,
        ExitMode mode, SyntaxNode? read = null)
    {
        if (mode == ExitMode.FlushProof && read is null) throw new ArgumentNullException(nameof(read));
        foreach (var node in statement.DescendantNodesAndSelf(node =>
                     node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax
                     && (node is not CatchClauseSyntax handler || CanEnterHandler(semanticModel, handler))))
        {
            // A terminal path cannot invalidate a flush proof for a read it never reaches.
            // Disposal must release values even on terminal paths.
            if (mode == ExitMode.FlushProof && ExitsBeforeRead(semanticModel, node, read!))
            {
                continue;
            }

            // Handler exits are visited separately below. A throw guaranteed to be
            // caught inside this statement does not itself bypass its successor.
            if (node is ThrowStatementSyntax caughtThrow
                && IsCaughtInsideStatement(semanticModel, caughtThrow, statement))
            {
                continue;
            }

            if (node is BreakStatementSyntax or ContinueStatementSyntax)
            {
                var target = ResolveJumpTarget(node);
                // A jump confined to this statement still reaches its successor.
                if (target is null || !statement.Span.Contains(target.Span))
                {
                    return true;
                }
            }
            else if (node is YieldStatementSyntax yieldStatement)
            {
                // Reads after suspension still cross a following flush. Disposal
                // must also cover callers abandoning the iterator at yield return.
                if (mode == ExitMode.Disposal || yieldStatement.IsKind(SyntaxKind.YieldBreakStatement))
                {
                    return true;
                }
            }
            else if (node is ReturnStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax)
            {
                // Goto remains a conservative bypass even when its target is local.
                return true;
            }
        }

        return false;
    }

    private static bool ExitsBeforeRead(SemanticModel semanticModel, SyntaxNode node, SyntaxNode read)
    {
        if (node is not ReturnStatementSyntax and not ThrowStatementSyntax
            and not BreakStatementSyntax and not ContinueStatementSyntax
            && !node.IsKind(SyntaxKind.YieldBreakStatement))
        {
            return false;
        }

        var enclosingTries = node.Ancestors().OfType<TryStatementSyntax>().ToArray();
        // Returning or throwing still executes enclosing finally blocks.
        if (enclosingTries.Any(enclosingTry => enclosingTry.Finally?.Span.Contains(read.Span) == true))
        {
            return false;
        }

        if (node is BreakStatementSyntax or ContinueStatementSyntax)
        {
            var loopBody = ResolveJumpTarget(node) switch
            {
                ForStatementSyntax loop => loop.Statement,
                CommonForEachStatementSyntax loop => loop.Statement,
                WhileStatementSyntax loop => loop.Statement,
                DoStatementSyntax loop => loop.Statement,
                _ => null,
            };
            // The jump skips the rest of this iteration. Reads in a loop condition,
            // increment, enclosing finally, or after the loop are not covered by this proof.
            return loopBody?.Span.Contains(read.Span) == true && read.SpanStart > node.Span.End;
        }

        if (node is not ThrowStatementSyntax thrown) return true;
        var thrownType = GetKnownThrownType(semanticModel, thrown);
        foreach (var enclosingTry in enclosingTries)
        {
            if (!enclosingTry.Block.Span.Contains(node.Span)) continue;
            foreach (var handler in enclosingTry.Catches)
            {
                var match = MatchCatch(semanticModel, thrownType, handler);
                if (match == CatchMatch.None) continue;
                if (!CatchExitsBeforeRead(semanticModel, handler, read)) return false;
                // A guaranteed handler prevents this throw reaching later or outer
                // catches. Throws from the handler itself are checked recursively.
                if (match == CatchMatch.Guaranteed) return true;
            }
        }

        return true;
    }

    private static bool CatchExitsBeforeRead(
        SemanticModel semanticModel, CatchClauseSyntax handler, SyntaxNode read)
    {
        if (handler.Span.Contains(read.Span)
            || semanticModel.AnalyzeControlFlow(handler.Block) is not { Succeeded: true, EndPointIsReachable: false } flow)
        {
            return false;
        }

        return flow.ExitPoints.All(exit => ExitsBeforeRead(semanticModel, exit, read));
    }

    private static bool IsCaughtInsideStatement(
        SemanticModel semanticModel, ThrowStatementSyntax thrown, StatementSyntax statement)
    {
        var thrownType = GetKnownThrownType(semanticModel, thrown);
        foreach (var enclosingTry in thrown.Ancestors().OfType<TryStatementSyntax>())
        {
            if (!statement.Span.Contains(enclosingTry.Span))
            {
                break;
            }

            if (!enclosingTry.Block.Span.Contains(thrown.Span))
            {
                continue;
            }

            if (enclosingTry.Catches.Any(handler => MatchCatch(semanticModel, thrownType, handler) == CatchMatch.Guaranteed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// False only when every exception the try block can raise is a known throw that this
    /// handler cannot catch, or that an earlier handler always catches. Any operation outside
    /// a small non-throwing set keeps the handler reachable.
    /// </summary>
    private static bool CanEnterHandler(SemanticModel semanticModel, CatchClauseSyntax handler)
    {
        if (handler.Parent is not TryStatementSyntax tryStatement
            || semanticModel.GetOperation(tryStatement.Block) is not { } body)
        {
            return true;
        }

        var throws = new List<ThrowStatementSyntax>();
        foreach (var operation in body.DescendantsAndSelf())
        {
            switch (operation)
            {
                case IThrowOperation { Syntax: ThrowStatementSyntax thrown }:
                    throws.Add(thrown);
                    break;
                case IConversionOperation { IsImplicit: true, Conversion.IsReference: true, Parent: IThrowOperation }:
                case IObjectCreationOperation { Parent: IThrowOperation or IConversionOperation { Parent: IThrowOperation } }:
                case IBlockOperation:
                case IConditionalOperation:
                case IExpressionStatementOperation { Operation: IThrowOperation }:
                case ILiteralOperation:
                case ILocalReferenceOperation:
                case IParameterReferenceOperation:
                case IArgumentOperation:
                case IUnaryOperation { OperatorMethod: null, OperatorKind: UnaryOperatorKind.Not }:
                case IBinaryOperation { OperatorMethod: null, OperatorKind:
                    BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr
                    or BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }:
                    break;
                default:
                    return true;
            }
        }

        var precedingHandlers = tryStatement.Catches.TakeWhile(candidate => candidate != handler).ToArray();
        return throws.Any(thrown =>
        {
            var thrownType = GetKnownThrownType(semanticModel, thrown);
            return precedingHandlers.All(previous => MatchCatch(semanticModel, thrownType, previous) != CatchMatch.Guaranteed)
                   && MatchCatch(semanticModel, thrownType, handler) != CatchMatch.None;
        });
    }

    private static ITypeSymbol? GetKnownThrownType(SemanticModel semanticModel, ThrowStatementSyntax thrown)
    {
        // User constructors (including new T()) and argument evaluation can throw a
        // different exception before the explicit throw. Keep their catches possible.
        if (thrown.Expression is not { } expression
            || semanticModel.GetOperation(Unwrap(expression)) is not IObjectCreationOperation creation
            || creation.Initializer is not null
            || creation.Arguments.Any(argument => !argument.Value.ConstantValue.HasValue
                && argument.Value is not ILocalReferenceOperation and not IParameterReferenceOperation))
            return null;

        // These framework constructors only store the supplied message/inner exception.
        // Other constructors remain opaque; this is not an interprocedural exception proof.
        foreach (var name in SimpleExceptionTypes)
            if (SymbolEqualityComparer.Default.Equals(creation.Type, semanticModel.Compilation.GetTypeByMetadataName(name)))
                return creation.Type;
        return null;
    }

    private static CatchMatch MatchCatch(
        SemanticModel semanticModel, ITypeSymbol? thrownType, CatchClauseSyntax handler)
    {
        // The graph walker models normal/finally successors. Keep catch dispatch
        // explicit until its exception-region traversal also models these paths.
        var filterIsGuaranteed = handler.Filter is null;
        if (handler.Filter is { } filter
            && semanticModel.GetConstantValue(filter.FilterExpression) is { HasValue: true, Value: bool matches })
        {
            if (!matches) return CatchMatch.None;
            filterIsGuaranteed = true;
        }

        if (handler.Declaration is not null)
        {
            if (semanticModel.GetTypeInfo(handler.Declaration.Type).Type is not { } caughtType)
                return CatchMatch.Possible;
            if (SymbolEqualityComparer.Default.Equals(caughtType,
                    semanticModel.Compilation.GetTypeByMetadataName("System.Exception")))
                return filterIsGuaranteed ? CatchMatch.Guaranteed : CatchMatch.Possible;
            if (thrownType is null) return CatchMatch.Possible;
            if (semanticModel.Compilation.ClassifyConversion(thrownType, caughtType) is not
                ({ IsImplicit: true, IsReference: true } or { IsIdentity: true }))
                return CatchMatch.None;
        }

        return filterIsGuaranteed ? CatchMatch.Guaranteed : CatchMatch.Possible;
    }

    private static SyntaxNode? ResolveJumpTarget(SyntaxNode jump)
        => jump.Ancestors().FirstOrDefault(ancestor =>
            ancestor is ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
            || (jump is BreakStatementSyntax && ancestor is SwitchStatementSyntax));

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

        if (FindBlock(graph, before) is not { } beforeBlock
            || FindBlock(graph, after) is not { } afterBlock)
        {
            return false;
        }

        if (beforeBlock.Ordinal == afterBlock.Ordinal)
        {
            return before.SpanStart < after.SpanStart;
        }

        return PathExistsAvoiding(graph, semanticModel.Compilation, graph.Blocks[0], int.MinValue, afterBlock, after.SpanStart, [])
               && !PathExistsAvoiding(graph, semanticModel.Compilation, graph.Blocks[0], int.MinValue, afterBlock, after.SpanStart, [before]);
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
            graph, semanticModel.Compilation, beforeBlock, before.SpanStart, afterBlock, after.SpanStart, []);
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
            graph, semanticModel.Compilation, beforeBlock, before.SpanStart, afterBlock, after.SpanStart, barriers, startPolicy);
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
            graph, semanticModel.Compilation, graph.Blocks[0], int.MinValue, afterBlock, after.SpanStart, barrierArray);
    }

    /// <summary>True when every path from <paramref name="before"/> to exit crosses one of <paramref name="barriers"/>.</summary>
    public static bool CollectivelyPostDominates(
        SemanticModel semanticModel,
        SyntaxNode scope,
        SyntaxNode before,
        IEnumerable<SyntaxNode> barriers,
        CancellationToken cancellationToken,
        BarrierStartPolicy startPolicy = BarrierStartPolicy.Exclude)
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
            semanticModel.Compilation,
            beforeBlock,
            before.SpanStart,
            graph.Blocks[graph.Blocks.Length - 1],
            int.MaxValue,
            barrierArray, startPolicy);
    }

    /// <summary>True when a local or parameter used by a condition is written between two nodes.</summary>
    public static bool HasWriteBetween(
        SemanticModel semanticModel,
        SyntaxNode scope,
        ExpressionSyntax condition,
        SyntaxNode before,
        SyntaxNode after,
        CancellationToken cancellationToken)
    {
        var symbols = condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(identifier => semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol)
            .Where(static symbol => symbol is ILocalSymbol or IParameterSymbol)
            .Distinct(SymbolEqualityComparer.Default);

        foreach (var symbol in symbols)
        {
            foreach (var reference in FindReferences(scope, symbol!, semanticModel, cancellationToken))
            {
                if (reference.SpanStart > before.Span.End
                    && reference.SpanStart < after.SpanStart
                    && IsWrite(reference))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsWrite(IdentifierNameSyntax reference)
    {
        if (reference.FirstAncestorOrSelf<AssignmentExpressionSyntax>() is { } assignment
            && assignment.Left.Span.Contains(reference.Span))
        {
            return true;
        }

        return reference.Parent switch
        {
            PrefixUnaryExpressionSyntax prefix =>
                prefix.IsKind(SyntaxKind.PreIncrementExpression)
                || prefix.IsKind(SyntaxKind.PreDecrementExpression),
            PostfixUnaryExpressionSyntax postfix =>
                postfix.IsKind(SyntaxKind.PostIncrementExpression)
                || postfix.IsKind(SyntaxKind.PostDecrementExpression),
            ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None),
            _ => false,
        };
    }

    private static bool PathExistsAvoiding(
        ControlFlowGraph graph,
        Compilation compilation,
        BasicBlock startBlock,
        int startPosition,
        BasicBlock targetBlock,
        int targetPosition,
        IEnumerable<SyntaxNode> barriers,
        BarrierStartPolicy startPolicy = BarrierStartPolicy.Exclude)
        => new ReachabilityWalker(graph, compilation, startBlock, startPosition, targetBlock,
            targetPosition, barriers, startPolicy).Search();

    private sealed class ReachabilityWalker(
        ControlFlowGraph graph,
        Compilation compilation,
        BasicBlock startBlock,
        int startPosition,
        BasicBlock targetBlock,
        int targetPosition,
        IEnumerable<SyntaxNode> barriers,
        BarrierStartPolicy startPolicy)
    {
        private readonly INamedTypeSymbol? _systemException = compilation.GetTypeByMetadataName("System.Exception");
        private readonly Dictionary<int, List<int>> _barrierPositions = new();
        // Interned continuations keep each finally's return destination in the search state.
        private readonly List<(int Block, int Next, ControlFlowRegion? Finally)> _continuations = [(-1, 0, null)];
        private readonly Dictionary<(int Block, int Next, ControlFlowRegion Finally), int> _continuationIds = new();
        private readonly Stack<(BasicBlock Block, int EntryPosition, int Continuation, bool Started)> _pending = new();
        private readonly Dictionary<(int Block, int Continuation, bool Started), int> _earliestEntries = new();

        internal bool Search()
        {
            foreach (var barrier in barriers)
            {
                if (FindBlock(graph, barrier) is not { } block)
                {
                    continue;
                }

                if (!_barrierPositions.TryGetValue(block.Ordinal, out var positions))
                {
                    positions = [];
                    _barrierPositions.Add(block.Ordinal, positions);
                }

                positions.Add(barrier.SpanStart);
            }

            var startsInFinally = false;
            for (var region = startBlock.EnclosingRegion; region is not null; region = region.EnclosingRegion)
            {
                startsInFinally |= region.Kind == ControlFlowRegionKind.Finally;
            }

            // Discover the actual continuation when the origin itself is inside a finally.
            _pending.Push(startsInFinally
                ? (graph.Blocks[0], int.MinValue, 0, false)
                : (startBlock, startPosition, 0, true));
            while (_pending.Count > 0)
            {
                var (block, entryPosition, continuation, started) = _pending.Pop();
                if (!started && block.Ordinal == startBlock.Ordinal)
                {
                    started = true;
                    entryPosition = startPosition;
                }

                var state = (block.Ordinal, continuation, started);
                if (_earliestEntries.TryGetValue(state, out var earliestEntry)
                    && earliestEntry <= entryPosition)
                {
                    continue;
                }

                _earliestEntries[state] = entryPosition;

                var firstBarrier = started && _barrierPositions.TryGetValue(block.Ordinal, out var positions)
                    ? positions.Where(position => position > entryPosition
                                                   || startPolicy == BarrierStartPolicy.Include && block.Ordinal == startBlock.Ordinal
                                                   && entryPosition == startPosition && position == entryPosition)
                        .DefaultIfEmpty(int.MaxValue).Min()
                    : int.MaxValue;

                if (started && block.Ordinal == targetBlock.Ordinal
                    && targetPosition > entryPosition
                    && targetPosition <= firstBarrier)
                {
                    return true;
                }

                if (firstBarrier != int.MaxValue)
                {
                    continue;
                }

                Follow(block.FallThroughSuccessor, continuation, started);
                Follow(block.ConditionalSuccessor, continuation, started);
            }

            return false;
        }

        private int Prepend(BasicBlock? destination, int continuation, ControlFlowRegion finalizer)
        {
            var key = (destination?.Ordinal ?? -1, continuation, finalizer);
            if (!_continuationIds.TryGetValue(key, out var id))
            {
                id = _continuations.Count;
                _continuations.Add(key);
                _continuationIds.Add(key, id);
            }

            return id;
        }

        private void Enqueue(BasicBlock? destination, IEnumerable<ControlFlowRegion> finalizers, int continuation, bool started)
        {
            foreach (var finalizer in finalizers.Reverse())
            {
                continuation = Prepend(destination, continuation, finalizer);
                destination = graph.Blocks[finalizer.FirstBlockOrdinal];
            }

            if (destination is not null && (destination.IsReachable || destination.Kind == BasicBlockKind.Exit))
            {
                _pending.Push((destination, int.MinValue, continuation, started));
            }
        }

        private void Follow(ControlFlowBranch? branch, int continuation, bool started)
        {
            if (branch is null)
            {
                return;
            }

            if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling)
            {
                // A rejected exception filter also uses this branch kind. It does not
                // complete an active finally; other handlers are explored separately.
                if (branch.Source.EnclosingRegion.Kind == ControlFlowRegionKind.Filter)
                {
                    return;
                }

                var resume = _continuations[continuation];
                if (resume.Block >= 0)
                {
                    _pending.Push((graph.Blocks[resume.Block], int.MinValue, resume.Next, started));
                }

                return;
            }

            var finalizers = branch.FinallyRegions.AsEnumerable();
            if (branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
            {
                // Throw branches have no destination or FinallyRegions. Their enclosing
                // protected regions still unwind, from the innermost finally outward.
                var unwind = new List<ControlFlowRegion>();
                var exception = branch.Source.BranchValue;
                while (exception is IConversionOperation conversion)
                {
                    exception = conversion.Operand;
                }

                for (var region = branch.Source.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                {
                    if (region.Kind == ControlFlowRegionKind.Try
                        && region.EnclosingRegion?.Kind == ControlFlowRegionKind.TryAndCatch)
                    {
                        foreach (var handler in region.EnclosingRegion.NestedRegions.Where(static nested =>
                                     nested.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler))
                        {
                            var (possible, certain) = GetCatchApplicability(handler, exception, _systemException);
                            if (possible)
                            {
                                var catchContinuation = continuation;
                                while (catchContinuation != 0)
                                {
                                    var activeFinally = _continuations[catchContinuation].Finally!;
                                    if (handler.FirstBlockOrdinal >= activeFinally.FirstBlockOrdinal
                                        && handler.LastBlockOrdinal <= activeFinally.LastBlockOrdinal)
                                    {
                                        break;
                                    }

                                    catchContinuation = _continuations[catchContinuation].Next;
                                }

                                Enqueue(graph.Blocks[handler.FirstBlockOrdinal], unwind, catchContinuation, started);
                            }

                            if (certain && handler.Kind == ControlFlowRegionKind.Catch)
                            {
                                return;
                            }
                        }
                    }

                    if (region.Kind == ControlFlowRegionKind.Try
                        && region.EnclosingRegion?.Kind == ControlFlowRegionKind.TryAndFinally)
                    {
                        unwind.Add(region.EnclosingRegion.NestedRegions.Last());
                    }
                }

                finalizers = unwind;
                continuation = 0;
            }

            var destination = branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow
                ? graph.Blocks[graph.Blocks.Length - 1]
                : branch.Destination;
            Enqueue(destination, finalizers, continuation, started);
        }

        private static (bool Possible, bool Certain) GetCatchApplicability(
            ControlFlowRegion handler, IOperation? exception, INamedTypeSymbol? systemException)
        {
            // A filtered region wraps separate filter/catch regions. The catch type still
            // restricts entry to the filter; a filter does not make incompatible types reachable.
            var catchRegion = handler.Kind == ControlFlowRegionKind.FilterAndHandler
                ? handler.NestedRegions.FirstOrDefault(static region => region.Kind == ControlFlowRegionKind.Catch)
                : handler;
            var catchType = catchRegion?.ExceptionType;
            var catchesAll = catchType is null || catchType.SpecialType == SpecialType.System_Object
                || SymbolEqualityComparer.Default.Equals(catchType, systemException);
            var exceptionType = exception?.Type;
            var certain = catchesAll || HasBaseType(exceptionType, catchType);
            // Unknown/rethrown/dynamic values and type parameters intentionally retain possible
            // handlers. A fresh construction has an exact type; other values may be derived.
            var possible = certain || exceptionType is null or ITypeParameterSymbol or IDynamicTypeSymbol
                || catchType is ITypeParameterSymbol
                || exception is not IObjectCreationOperation && HasBaseType(catchType, exceptionType);
            return (possible, certain);
        }

        private static bool HasBaseType(ITypeSymbol? type, ITypeSymbol? expected)
        {
            for (; type is not null; type = type.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, expected))
                {
                    return true;
                }
            }
            return false;
        }
    }

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
