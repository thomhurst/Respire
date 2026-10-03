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
                            var (possible, certain) = GetCatchApplicability(handler, exception, compilation, _systemException);
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
            ControlFlowRegion handler, IOperation? exception, Compilation compilation, INamedTypeSymbol? systemException)
        {
            // A filtered region wraps separate filter/catch regions. The catch type still
            // restricts entry to the filter; a filter does not make incompatible types reachable.
            var catchRegion = handler.Kind == ControlFlowRegionKind.FilterAndHandler
                ? handler.NestedRegions.FirstOrDefault(static region => region.Kind == ControlFlowRegionKind.Catch)
                : handler;
            var catchType = catchRegion?.ExceptionType;
            var catchesAll = catchType is null || catchType.SpecialType == SpecialType.System_Object
                || SymbolEqualityComparer.Default.Equals(catchType, systemException);
            // An opaque construction can raise any exception before the explicit throw, so
            // every handler stays possible and only a catch-all is certain to handle it.
            if (exception is IObjectCreationOperation && ScopeExitAnalysis.GetKnownExactExceptionType(compilation, exception) is null)
            {
                return (true, catchesAll);
            }

            var exceptionType = exception?.Type;
            // A type parameter's runtime type derives from its class constraint. Without
            // one, the value stays unknown and every handler remains possible.
            if (exceptionType is ITypeParameterSymbol typeParameter)
            {
                exceptionType = typeParameter.ConstraintTypes.FirstOrDefault(static constraint => constraint.TypeKind == TypeKind.Class);
            }

            // Throwing an existing value that is null raises NullReferenceException instead.
            var nullReference = exception is null or IObjectCreationOperation or ITypeParameterObjectCreationOperation
                ? null
                : compilation.GetTypeByMetadataName("System.NullReferenceException");
            var certain = catchesAll || HasBaseType(exceptionType, catchType)
                && (exception is IObjectCreationOperation or ITypeParameterObjectCreationOperation || HasBaseType(nullReference, catchType));
            // Unknown/rethrown/dynamic values intentionally retain possible handlers. A known
            // simple construction has an exact type; other values may be derived or null.
            var possible = certain || exceptionType is null or IDynamicTypeSymbol
                || catchType is ITypeParameterSymbol
                || exception is not IObjectCreationOperation
                   && (HasBaseType(catchType, exceptionType) || HasBaseType(nullReference, catchType));
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
