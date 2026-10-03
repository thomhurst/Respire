using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

internal static partial class ScopeWalker
{
    private sealed class ReachabilityWalker(
        ControlFlowGraph graph,
        INamedTypeSymbol? systemException,
        BasicBlock startBlock,
        int startPosition,
        BasicBlock targetBlock,
        int targetPosition,
        IEnumerable<SyntaxNode> barriers,
        BarrierStartPolicy startPolicy,
        CancellationToken cancellationToken)
    {
        private readonly INamedTypeSymbol? _systemException = systemException;
        private readonly FlowConditions _conditions = new(graph, cancellationToken);
        private readonly Dictionary<int, List<int>> _barrierPositions = new();
        // Interned continuations keep each finally's return destination in the search state.
        private readonly List<(int Block, int Next, ControlFlowRegion? Finally)> _continuations = [(-1, 0, null)];
        private readonly Dictionary<(int Block, int Next, ControlFlowRegion Finally), int> _continuationIds = new();
        private readonly Stack<(BasicBlock Block, int EntryPosition, int Continuation, bool Started, ulong Known, ulong Values, int Dispatch)> _pending = new();
        private readonly Dictionary<(int Block, int Continuation, bool Started, ulong Known, ulong Values, int Dispatch), int> _earliestEntries = new();

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

            // Start at entry so reaching an origin retains the branch that selected it.
            // Catch origins may also be entered by implicit exceptions. Discover those entries
            // from their protected blocks so a catch nested in a finally keeps its continuation.
            var catchOrigins = new List<(ControlFlowRegion Handler, ControlFlowRegion Protected)>();
            for (var region = startBlock.EnclosingRegion; region is not null; region = region.EnclosingRegion)
            {
                if (region.Kind != ControlFlowRegionKind.Catch)
                    continue;
                var owner = region.EnclosingRegion;
                if (owner?.Kind == ControlFlowRegionKind.FilterAndHandler)
                    owner = owner.EnclosingRegion;
                if (owner?.Kind == ControlFlowRegionKind.TryAndCatch)
                    catchOrigins.Add((region, owner.NestedRegions.First(static nested => nested.Kind == ControlFlowRegionKind.Try)));
            }
            _pending.Push((graph.Blocks[0], int.MinValue, 0, false, 0, 0, 0));
            var remaining = 16384;
            while (_pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Exhaustion is a possible path, never a proof of safety.
                if (--remaining == 0)
                    return true;
                var (block, entryPosition, continuation, started, known, values, dispatch) = _pending.Pop();
                if (!started && block.Ordinal == startBlock.Ordinal)
                {
                    started = true;
                    entryPosition = startPosition;
                }

                var state = (block.Ordinal, continuation, started, known, values, dispatch);
                if (_earliestEntries.TryGetValue(state, out var earliestEntry)
                    && earliestEntry <= entryPosition)
                {
                    continue;
                }

                _earliestEntries[state] = entryPosition;

                if (!started)
                {
                    foreach (var origin in catchOrigins)
                    {
                        if (block.Ordinal < origin.Protected.FirstBlockOrdinal
                            || block.Ordinal > origin.Protected.LastBlockOrdinal)
                            continue;
                        var unwind = new List<ControlFlowRegion>();
                        for (var region = block.EnclosingRegion; region is not null && region != origin.Protected;
                             region = region.EnclosingRegion)
                            if (region.Kind == ControlFlowRegionKind.Try
                                && region.EnclosingRegion?.Kind == ControlFlowRegionKind.TryAndFinally)
                                unwind.Add(region.EnclosingRegion.NestedRegions.Last());
                        Enqueue(graph.Blocks[origin.Handler.FirstBlockOrdinal], unwind,
                            CatchContinuation(origin.Handler, continuation), false, 0, 0, 0);
                    }
                }

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

                // An iterator can be disposed at any suspension. Its finally regions still
                // run, but ordinary statements after yield return need not execute.
                foreach (var operation in block.Operations)
                {
                    if (operation.Kind == OperationKind.YieldReturn
                        && operation.Syntax.SpanStart > entryPosition
                        && operation.Syntax.SpanStart < firstBarrier)
                    {
                        var unwind = new List<ControlFlowRegion>();
                        for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                            if (region.Kind == ControlFlowRegionKind.Try
                                && region.EnclosingRegion?.Kind == ControlFlowRegionKind.TryAndFinally)
                                unwind.Add(region.EnclosingRegion.NestedRegions.Last());
                        Enqueue(graph.Blocks[graph.Blocks.Length - 1], unwind, 0, started, known, values, 0);
                    }
                }

                if (firstBarrier != int.MaxValue)
                {
                    continue;
                }

                Follow(block.FallThroughSuccessor, continuation, started, known, values, dispatch);
                Follow(block.ConditionalSuccessor, continuation, started, known, values, dispatch);
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

        private void Enqueue(BasicBlock? destination, IEnumerable<ControlFlowRegion> finalizers, int continuation, bool started, ulong known, ulong values, int dispatch)
        {
            foreach (var finalizer in finalizers.Reverse())
            {
                continuation = Prepend(destination, continuation, finalizer);
                destination = graph.Blocks[finalizer.FirstBlockOrdinal];
            }

            if (destination is not null && (destination.IsReachable || destination.Kind == BasicBlockKind.Exit))
            {
                _pending.Push((destination, int.MinValue, continuation, started, known, values, dispatch));
            }
        }

        private void Follow(ControlFlowBranch? branch, int continuation, bool started, ulong known, ulong values, int dispatch)
        {
            if (branch is null)
            {
                return;
            }

            if (branch.Source.ConditionKind != ControlFlowConditionKind.None)
            {
                var expected = branch.IsConditionalSuccessor
                    == (branch.Source.ConditionKind == ControlFlowConditionKind.WhenTrue);
                if (!_conditions.Constrain(branch.Source.BranchValue, expected, ref known, ref values))
                    return;
            }

            if (dispatch != 0)
            {
                var candidate = _dispatches[dispatch]!;
                if (branch.Semantics is ControlFlowBranchSemantics.StructuredExceptionHandling
                    or ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
                {
                    // A false or throwing filter resumes the original exception search.
                    Dispatch(candidate.Next, started, known, values);
                    return;
                }
                if (branch.Destination?.Ordinal == candidate.Handler!.FirstBlockOrdinal)
                {
                    // Filters execute during the search pass. Only after a handler accepts
                    // do the intervening finally regions execute during stack unwinding.
                    Enqueue(branch.Destination, candidate.Unwind, candidate.Continuation,
                        started, known, values, 0);
                    return;
                }
            }

            if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling)
            {
                var resume = _continuations[continuation];
                if (resume.Block >= 0)
                    _pending.Push((graph.Blocks[resume.Block], int.MinValue, resume.Next, started, known, values, dispatch));
                return;
            }

            if (branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
            {
                Dispatch(GetDispatch(branch, continuation), started, known, values);
                return;
            }

            var finalizers = branch.FinallyRegions.AsEnumerable();
            var destination = branch.Destination;
            // Do not carry a selection from one loop iteration into the next. In particular,
            // a newly acquired owner must not inherit a previous iteration's flush proof.
            if (destination is not null && destination.Ordinal <= branch.Source.Ordinal)
                known = values = 0;
            Enqueue(destination, finalizers, continuation, started, known, values, dispatch);
        }

        private sealed class CatchDispatch(
            ControlFlowRegion? handler, ControlFlowRegion? filter, ControlFlowRegion[] unwind,
            int continuation, int next, bool certain)
        {
            internal ControlFlowRegion? Handler { get; } = handler;
            internal ControlFlowRegion? Filter { get; } = filter;
            internal ControlFlowRegion[] Unwind { get; } = unwind;
            internal int Continuation { get; } = continuation;
            internal int Next { get; } = next;
            internal bool Certain { get; } = certain;
        }

        private readonly List<CatchDispatch?> _dispatches = [null];
        private readonly Dictionary<(int Block, int Continuation), int> _dispatchIds = new();

        private int GetDispatch(ControlFlowBranch branch, int continuation)
        {
            var key = (branch.Source.Ordinal, continuation);
            if (_dispatchIds.TryGetValue(key, out var existing))
                return existing;

            var exception = branch.Source.BranchValue;
            while (exception is IConversionOperation conversion)
                exception = conversion.Operand;
            ITypeSymbol? exceptionType = exception?.Type;
            var exactType = exception is IObjectCreationOperation;
            if (exception?.ConstantValue is { HasValue: true, Value: null })
            {
                exceptionType = _systemException?.ContainingNamespace.GetTypeMembers("NullReferenceException").FirstOrDefault();
                exactType = true;
            }
            if (branch.Semantics == ControlFlowBranchSemantics.Rethrow)
            {
                for (var region = branch.Source.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                {
                    if (region.Kind == ControlFlowRegionKind.Catch)
                    {
                        exceptionType = region.ExceptionType;
                        break;
                    }
                }
            }

            var unwind = new List<ControlFlowRegion>();
            var candidates = new List<(ControlFlowRegion Handler, ControlFlowRegion? Filter,
                ControlFlowRegion[] Unwind, int Continuation, bool Certain)>();
            for (var region = branch.Source.EnclosingRegion; region is not null; region = region.EnclosingRegion)
            {
                if (region.Kind == ControlFlowRegionKind.Try
                    && region.EnclosingRegion?.Kind == ControlFlowRegionKind.TryAndCatch)
                {
                    foreach (var entry in region.EnclosingRegion.NestedRegions.Where(static nested =>
                                 nested.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler))
                    {
                        var handler = entry.Kind == ControlFlowRegionKind.Catch ? entry
                            : entry.NestedRegions.First(static nested => nested.Kind == ControlFlowRegionKind.Catch);
                        var filter = entry.Kind == ControlFlowRegionKind.FilterAndHandler
                            ? entry.NestedRegions.First(static nested => nested.Kind == ControlFlowRegionKind.Filter) : null;
                        var (possible, certain) = GetCatchApplicability(handler, exceptionType,
                            exactType, _systemException);
                        if (!possible)
                            continue;
                        candidates.Add((handler, filter, unwind.ToArray(), CatchContinuation(handler, continuation), certain));
                    }
                }
                if (region.Kind == ControlFlowRegionKind.Try
                    && region.EnclosingRegion?.Kind == ControlFlowRegionKind.TryAndFinally)
                    unwind.Add(region.EnclosingRegion.NestedRegions.Last());
            }

            var next = _dispatches.Count;
            _dispatches.Add(new CatchDispatch(null, null, unwind.ToArray(), 0, 0, true));
            foreach (var candidate in candidates.AsEnumerable().Reverse())
            {
                var id = _dispatches.Count;
                _dispatches.Add(new CatchDispatch(candidate.Handler, candidate.Filter, candidate.Unwind,
                    candidate.Continuation, next, candidate.Certain));
                next = id;
            }
            _dispatchIds.Add(key, next);
            return next;
        }

        private int CatchContinuation(ControlFlowRegion handler, int continuation)
        {
            while (continuation != 0)
            {
                var activeFinally = _continuations[continuation].Finally!;
                if (handler.FirstBlockOrdinal >= activeFinally.FirstBlockOrdinal
                    && handler.LastBlockOrdinal <= activeFinally.LastBlockOrdinal)
                    break;
                continuation = _continuations[continuation].Next;
            }
            return continuation;
        }

        private void Dispatch(int id, bool started, ulong known, ulong values)
        {
            // Type tests are ordered. Unknown runtime types retain both possibilities; a
            // certain match reaches later handlers only when its filter rejects the exception.
            while (id != 0)
            {
                var candidate = _dispatches[id]!;
                if (candidate.Filter is { } filter)
                    Enqueue(graph.Blocks[filter.FirstBlockOrdinal], [], candidate.Continuation,
                        started, known, values, id);
                else
                    Enqueue(candidate.Handler is { } handler ? graph.Blocks[handler.FirstBlockOrdinal]
                            : graph.Blocks[graph.Blocks.Length - 1],
                        candidate.Unwind, candidate.Continuation, started, known, values, 0);
                if (candidate.Certain)
                    return;
                id = candidate.Next;
            }
        }

        private static (bool Possible, bool Certain) GetCatchApplicability(
            ControlFlowRegion handler, ITypeSymbol? exceptionType, bool exactType, INamedTypeSymbol? systemException)
        {
            var catchType = handler.ExceptionType;
            var catchesAll = catchType is null || catchType.SpecialType == SpecialType.System_Object
                || SymbolEqualityComparer.Default.Equals(catchType, systemException);
            var certain = catchesAll || HasBaseType(exceptionType, catchType);
            // Unknown/rethrown/dynamic values and type parameters intentionally retain possible
            // handlers. A fresh construction has an exact type; other values may be derived.
            var possible = certain || exceptionType is null or ITypeParameterSymbol or IDynamicTypeSymbol
                || catchType is ITypeParameterSymbol
                || !exactType && HasBaseType(catchType, exceptionType);
            return (possible, certain);
        }

        private static bool HasBaseType(ITypeSymbol? type, ITypeSymbol? expected, int depth = 0)
        {
            if (depth == 32)
                return false;
            for (; type is not null; type = type.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, expected))
                {
                    return true;
                }
                if (type is ITypeParameterSymbol parameter
                    && parameter.ConstraintTypes.Any(constraint => HasBaseType(constraint, expected, depth + 1)))
                    return true;
            }
            return false;
        }
    }

}
