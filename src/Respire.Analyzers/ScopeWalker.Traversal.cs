using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

internal static partial class ScopeWalker
{
    private sealed class ReachabilityWalker(
        ControlFlowGraph graph,
        SemanticModel semanticModel,
        BasicBlock startBlock,
        int startPosition,
        BasicBlock targetBlock,
        int targetPosition,
        IEnumerable<SyntaxNode> barriers,
        BarrierStartPolicy startPolicy,
        CancellationToken cancellationToken,
        SyntaxNode? origin)
    {
        private const int MaxProcessedStates = 16384;
        private readonly INamedTypeSymbol? _systemException = semanticModel.Compilation.GetTypeByMetadataName("System.Exception");
        private readonly FlowConditions _conditions = new(graph, startBlock, cancellationToken);
        private readonly Dictionary<int, List<int>> _barrierPositions = new();
        // Interned continuations keep each finally's return destination in the search state.
        private readonly List<(int Block, int Next, ControlFlowRegion? Finally)> _continuations = [(-1, 0, null)];
        private readonly Dictionary<(int Block, int Next, ControlFlowRegion Finally), int> _continuationIds = new();
        private readonly List<CatchDispatch?> _dispatches = [null];
        private readonly Dictionary<(int Block, int Continuation, bool NullPath, bool Implicit, bool AllocationOnly), int> _dispatchIds = new();
        private readonly Dictionary<(int Block, ControlFlowRegion Handler, int Continuation), int> _catchOriginDispatchIds = new();
        private readonly Dictionary<IOperation, bool> _throwingOperations = new();
        private readonly Stack<SearchState> _pending = new();
        private readonly Dictionary<SearchState, int> _earliestEntries = new();

        private readonly struct SearchState(
            BasicBlock block, int continuation, bool started, ulong known, ulong values, int dispatch) : IEquatable<SearchState>
        {
            internal BasicBlock Block { get; } = block;
            internal int Continuation { get; } = continuation;
            internal bool Started { get; } = started;
            internal ulong Known { get; } = known;
            internal ulong Values { get; } = values;
            internal int Dispatch { get; } = dispatch;

            public bool Equals(SearchState other) => Block == other.Block
                && Continuation == other.Continuation && Started == other.Started
                && Known == other.Known && Values == other.Values && Dispatch == other.Dispatch;

            public override bool Equals(object? obj) => obj is SearchState other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = Block.Ordinal;
                    hash = hash * 397 ^ Continuation;
                    hash = hash * 397 ^ Started.GetHashCode();
                    hash = hash * 397 ^ Known.GetHashCode();
                    hash = hash * 397 ^ Values.GetHashCode();
                    return hash * 397 ^ Dispatch;
                }
            }
        }

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

            foreach (var positions in _barrierPositions.Values)
                positions.Sort();
            var catchOrigins = FindCatchOrigins();
            // Start at entry so reaching an origin retains the branch that selected it.
            _pending.Push(new(graph.Blocks[0], continuation: 0, started: false, known: 0, values: 0, dispatch: 0));
            var remaining = MaxProcessedStates;
            while (_pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Exhaustion is a possible path, never a proof of safety.
                if (--remaining == 0)
                    return true;
                var pending = _pending.Pop();
                var block = pending.Block;
                var entryPosition = int.MinValue;
                var continuation = pending.Continuation;
                var started = pending.Started;
                var known = pending.Known;
                var values = pending.Values;
                var dispatch = pending.Dispatch;
                if (!started && block.Ordinal == startBlock.Ordinal)
                {
                    started = true;
                    entryPosition = startPosition;
                }

                var state = new SearchState(block, continuation, started, known, values, dispatch);
                if (_earliestEntries.TryGetValue(state, out var earliestEntry)
                    && earliestEntry <= entryPosition)
                {
                    continue;
                }

                _earliestEntries[state] = entryPosition;

                var firstBarrier = FindFirstBarrier(block, entryPosition, started);
                if (!started)
                    SeedCatchOrigins(block, catchOrigins, continuation, known, values);
                EnqueueImplicitExceptionPaths(block, entryPosition, firstBarrier, continuation,
                    started, dispatch, ref known, ref values);

                if (started && block.Ordinal == targetBlock.Ordinal
                    && targetPosition > entryPosition
                    && targetPosition <= firstBarrier)
                {
                    return true;
                }

                EnqueueIteratorDisposal(block, entryPosition, firstBarrier, started, known, values);

                if (firstBarrier != int.MaxValue)
                {
                    continue;
                }

                Follow(block.FallThroughSuccessor, continuation, started, known, values, dispatch);
                Follow(block.ConditionalSuccessor, continuation, started, known, values, dispatch);
            }

            return false;
        }

        private List<(ControlFlowRegion Handler, ControlFlowRegion Protected)> FindCatchOrigins()
        {
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
            return catchOrigins;
        }

        private void SeedCatchOrigins(BasicBlock block,
            List<(ControlFlowRegion Handler, ControlFlowRegion Protected)> catchOrigins,
            int continuation, ulong known, ulong values)
        {
            foreach (var origin in catchOrigins)
            {
                if (block.Ordinal < origin.Protected.FirstBlockOrdinal
                    || block.Ordinal > origin.Protected.LastBlockOrdinal)
                    continue;
                var unwind = CollectFinallyRegions(block.EnclosingRegion, origin.Protected);
                var catchContinuation = CatchContinuation(origin.Handler, continuation);
                if (origin.Handler.EnclosingRegion is { Kind: ControlFlowRegionKind.FilterAndHandler } filtered)
                {
                    var key = (block.Ordinal, origin.Handler, catchContinuation);
                    if (!_catchOriginDispatchIds.TryGetValue(key, out var id))
                    {
                        var filter = filtered.NestedRegions.First(static region => region.Kind == ControlFlowRegionKind.Filter);
                        id = _dispatches.Count;
                        _dispatches.Add(new CatchDispatch(origin.Handler, filter, unwind.ToArray(),
                            catchContinuation, next: 0, certain: true));
                        _catchOriginDispatchIds.Add(key, id);
                    }
                    // Even an unknown implicit exception must pass the filter before
                    // acquiring a value in its handler. Retain that selection evidence.
                    Dispatch(id, started: false, known, values);
                }
                else
                    Enqueue(graph.Blocks[origin.Handler.FirstBlockOrdinal], unwind,
                        catchContinuation, false, known, values, 0);
            }
        }

        private void EnqueueIteratorDisposal(BasicBlock block, int entryPosition, int firstBarrier,
            bool started, ulong known, ulong values)
        {
            // An iterator can be disposed at any suspension. Its finally regions still
            // run, but ordinary statements after yield return need not execute.
            foreach (var operation in block.Operations)
            {
                if (operation.Kind == OperationKind.YieldReturn
                    && operation.Syntax.SpanStart > entryPosition
                    && operation.Syntax.SpanStart < firstBarrier)
                {
                    var unwind = CollectFinallyRegions(block.EnclosingRegion);
                    Enqueue(graph.Blocks[graph.Blocks.Length - 1], unwind, 0, started, known, values, 0);
                }
            }
        }

        private int FindFirstBarrier(BasicBlock block, int entryPosition, bool started)
        {
            if (!started || !_barrierPositions.TryGetValue(block.Ordinal, out var positions))
                return int.MaxValue;
            var index = positions.BinarySearch(entryPosition);
            if (index < 0)
                index = ~index;
            else if (startPolicy != BarrierStartPolicy.Include || block != startBlock || entryPosition != startPosition)
            {
                while (index < positions.Count && positions[index] == entryPosition)
                    index++;
            }
            return index < positions.Count ? positions[index] : int.MaxValue;
        }

        private void EnqueueImplicitExceptionPaths(BasicBlock block, int entryPosition, int firstBarrier,
            int continuation, bool started, int dispatch, ref ulong known, ref ulong values)
        {
            foreach (var operation in block.Operations)
                Visit(operation, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
            if (block.BranchValue is { } branchValue)
                Visit(branchValue, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
        }

        private void Visit(IOperation operation, BasicBlock block, int entryPosition, int firstBarrier,
            int continuation, bool started, int dispatch, ref ulong known, ref ulong values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation or INameOfOperation
                || operation.Syntax.SpanStart >= firstBarrier)
                return;

            // Evaluate children before their parent's write. Each exception sees only writes
            // that have already executed, including writes in earlier call arguments.
            var exceptionSource = operation;
            var arrayAllocation = operation.Type is IArrayTypeSymbol
                && operation.Syntax is CollectionExpressionSyntax collection
                && !collection.Elements.Any(static element => element is SpreadElementSyntax);
            IOperation? initializer = operation switch
            {
                IObjectCreationOperation creation => creation.Initializer,
                ITypeParameterObjectCreationOperation creation => creation.Initializer,
                IDynamicObjectCreationOperation creation => creation.Initializer,
                IWithOperation copy => copy.Initializer,
                IArrayCreationOperation creation => creation.Initializer,
                _ => null,
            };
            if (operation is IAnonymousObjectCreationOperation anonymous)
            {
                // Anonymous properties are constructor arguments, not setter calls.
                foreach (var member in anonymous.Initializers)
                    if (member is ISimpleAssignmentOperation memberAssignment)
                        Visit(memberAssignment.Value, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
            }
            else if (operation is ISimpleAssignmentOperation { IsRef: false } assignment
                && assignment.Target is IPropertyReferenceOperation { Property.ReturnsByRef: false, Property.ReturnsByRefReadonly: false }
                    or IFieldReferenceOperation or IArrayElementReferenceOperation
                    or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation)
            {
                // Evaluate the receiver and indexes, then the RHS, then perform the store.
                // A property target is not a getter call in a simple assignment.
                foreach (var child in assignment.Target.ChildOperations)
                    Visit(child, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
                Visit(assignment.Value, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
                exceptionSource = assignment.Target;
            }
            else
            {
                foreach (var child in operation.ChildOperations)
                    if (child != initializer && !arrayAllocation)
                        Visit(child, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
                // Roslyn lowers conditional stores (including ??=) to assignments
                // through captured targets. Their setters still run after the RHS.
                if (operation is ISimpleAssignmentOperation { Target: IFlowCaptureReferenceOperation target })
                    exceptionSource = _conditions.ResolveCapturedTarget(target);
            }
            // Barrier failure and uncaught implicit exceptions remain outside this proof.
            if (operation.Syntax.SpanStart > entryPosition
                // Arguments and receivers inside the origin run before acquisition completes.
                && !(entryPosition == startPosition && origin?.Span.Contains(operation.Syntax.Span) == true)
                && operation.Syntax.Span.End <= firstBarrier && MayThrow(exceptionSource))
            {
                if (dispatch != 0)
                    // The runtime treats a throwing filter as a rejected filter.
                    Dispatch(_dispatches[dispatch]!.Next, started, known, values);
                else if (block.FallThroughSuccessor is { } successor)
                {
                    // Elements and arguments have their own exception paths. Fixed arrays
                    // and simple framework exception constructors only add allocation failure.
                    Dispatch(GetDispatch(successor, continuation, implicitException: true,
                        allocationOnly: arrayAllocation
                            || exceptionSource is IAnonymousObjectCreationOperation
                            || exceptionSource is IConversionOperation boxing && IsBoxing(boxing)
                            || ScopeExitAnalysis.GetKnownExactExceptionType(semanticModel.Compilation, exceptionSource) is not null),
                        started, known, values);
                }
            }
            // Construction/allocation can fail before any initializer runs.
            if (initializer is not null)
                Visit(initializer, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
            if (arrayAllocation)
                foreach (var child in operation.ChildOperations)
                    Visit(child, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
            _conditions.ForgetOwnWrite(operation, ref known, ref values);
        }

        private static List<ControlFlowRegion> CollectFinallyRegions(ControlFlowRegion from, ControlFlowRegion? until = null)
        {
            var finalizers = new List<ControlFlowRegion>();
            for (var region = from; region is not null && region != until; region = region.EnclosingRegion)
                if (region.Kind == ControlFlowRegionKind.Try
                    && region.EnclosingRegion?.Kind == ControlFlowRegionKind.TryAndFinally)
                    finalizers.Add(region.EnclosingRegion.NestedRegions.Last());
            return finalizers;
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
                _pending.Push(new(destination, continuation: continuation, started: started,
                    known: known, values: values, dispatch: dispatch));
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
                    _pending.Push(new(graph.Blocks[resume.Block], continuation: resume.Next, started: started,
                        known: known, values: values, dispatch: dispatch));
                return;
            }

            if (branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
            {
                Dispatch(GetDispatch(branch, continuation), started, known, values);
                if (branch.Semantics == ControlFlowBranchSemantics.Throw)
                {
                    var exception = UnwrapException(branch.Source.BranchValue);
                    if (exception is ILocalReferenceOperation or IParameterReferenceOperation
                        && ScopeExitAnalysis.GetExactThrownType(semanticModel, exception) is null)
                        Dispatch(GetDispatch(branch, continuation, nullPath: true), started, known, values);
                }
                return;
            }

            var finalizers = branch.FinallyRegions.AsEnumerable();
            var destination = branch.Destination;
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

        private int GetDispatch(ControlFlowBranch branch, int continuation, bool nullPath = false,
            bool implicitException = false, bool allocationOnly = false)
        {
            var key = (branch.Source.Ordinal, continuation, nullPath, implicitException, allocationOnly);
            if (_dispatchIds.TryGetValue(key, out var existing))
                return existing;

            var exception = UnwrapException(branch.Source.BranchValue);
            ITypeSymbol? exceptionType = ScopeExitAnalysis.GetExactThrownType(semanticModel, exception);
            var exactType = exceptionType is not null;
            var possiblyNull = !exactType && exception is ILocalReferenceOperation or IParameterReferenceOperation;
            exceptionType ??= possiblyNull ? exception?.Type : null;
            if (nullPath || exception?.ConstantValue is { HasValue: true, Value: null })
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
            if (implicitException)
            {
                exceptionType = allocationOnly
                    ? semanticModel.Compilation.GetTypeByMetadataName("System.OutOfMemoryException") : null;
                exactType = exceptionType is not null;
            }

            var unwind = CollectFinallyRegions(branch.Source.EnclosingRegion);
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
                        cancellationToken.ThrowIfCancellationRequested();
                        var handler = entry.Kind == ControlFlowRegionKind.Catch ? entry
                            : entry.NestedRegions.First(static nested => nested.Kind == ControlFlowRegionKind.Catch);
                        var filter = entry.Kind == ControlFlowRegionKind.FilterAndHandler
                            ? entry.NestedRegions.First(static nested => nested.Kind == ControlFlowRegionKind.Filter) : null;
                        var (possible, certain) = GetCatchApplicability(handler, exceptionType,
                            exactType, _systemException);
                        if (!possible)
                            continue;
                        candidates.Add((handler, filter, CollectFinallyRegions(branch.Source.EnclosingRegion, region).ToArray(),
                            CatchContinuation(handler, continuation), certain));
                    }
                }
            }

            var next = 0;
            if (!implicitException)
            {
                next = _dispatches.Count;
                _dispatches.Add(new CatchDispatch(null, null, unwind.ToArray(), 0, 0, true));
            }
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

        private bool MayThrow(IOperation operation)
        {
            if (_throwingOperations.TryGetValue(operation, out var cached))
                return cached;
            var throwing = operation is IInvocationOperation or IAwaitOperation or IPropertyReferenceOperation
                or IDynamicInvocationOperation or IArrayElementReferenceOperation or ITypeParameterObjectCreationOperation
                or IEventAssignmentOperation or IArrayCreationOperation
                or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation
                or IDynamicObjectCreationOperation
                or IWithOperation { CloneMethod: not null }
                or IRecursivePatternOperation { DeconstructSymbol: not null }
                or IBinaryOperation { OperatorMethod: not null }
                or IUnaryOperation { OperatorMethod: not null }
                or ICompoundAssignmentOperation { OperatorMethod: not null }
                or IIncrementOrDecrementOperation { OperatorMethod: not null }
                or IBinaryOperation { LeftOperand.Type.TypeKind: TypeKind.Dynamic }
                or IBinaryOperation { RightOperand.Type.TypeKind: TypeKind.Dynamic }
                or IUnaryOperation { Operand.Type.TypeKind: TypeKind.Dynamic }
                or ICompoundAssignmentOperation { Target.Type.TypeKind: TypeKind.Dynamic }
                or ICompoundAssignmentOperation { Value.Type.TypeKind: TypeKind.Dynamic }
                or IIncrementOrDecrementOperation { Target.Type.TypeKind: TypeKind.Dynamic }
                or ICoalesceAssignmentOperation { Target: IPropertyReferenceOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation }
                or ICompoundAssignmentOperation { Target: IPropertyReferenceOperation }
                or IIncrementOrDecrementOperation { Target: IPropertyReferenceOperation }
                or ICompoundAssignmentOperation { Target: IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation }
                or IIncrementOrDecrementOperation { Target: IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation }
                // Roslyn 4.8 exposes these syntax nodes without public operation interfaces.
                || operation.Syntax is CollectionExpressionSyntax or SpreadElementSyntax
                || operation is IListPatternOperation listPattern
                    && (listPattern.LengthSymbol is not null || listPattern.IndexerSymbol is not null)
                    && listPattern.InputType.TypeKind != TypeKind.Array
                    && listPattern.InputType.SpecialType != SpecialType.System_String
                || operation is ISlicePatternOperation { SliceSymbol: not null } slicePattern
                    && slicePattern.InputType.TypeKind != TypeKind.Array
                    && slicePattern.InputType.SpecialType != SpecialType.System_String
                || operation is IDelegateCreationOperation
                    { Target: IMethodReferenceOperation { Method.IsStatic: false, Instance: { } methodReceiver } }
                    && methodReceiver.Type?.IsReferenceType == true
                    && methodReceiver is not IInstanceReferenceOperation
                    && !_conditions.IsConstructedReceiver(methodReceiver)
                || !operation.ConstantValue.HasValue && (operation switch
                {
                    IBinaryOperation binary => ArithmeticMayThrow(binary.OperatorKind, binary.IsChecked, binary.Type),
                    ICompoundAssignmentOperation assignment => ArithmeticMayThrow(assignment.OperatorKind, assignment.IsChecked, assignment.Type),
                    IIncrementOrDecrementOperation increment => ArithmeticMayThrow(BinaryOperatorKind.Add, increment.IsChecked, increment.Type),
                    IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus } unary =>
                        unary.IsChecked && IsIntegral(unary.Type),
                    _ => false,
                })
                || operation is IFieldReferenceOperation { Field.IsStatic: false, Instance: { } receiver }
                    && receiver.Type?.IsReferenceType == true && receiver is not IInstanceReferenceOperation
                    && !_conditions.IsConstructedReceiver(receiver)
                    // A member binding is evaluated only on the non-null conditional-access path.
                    && operation.Syntax is not MemberBindingExpressionSyntax
                || operation is IFieldReferenceOperation { Field: { IsStatic: true, IsConst: false } field }
                    && field.ContainingType.StaticConstructors.Length != 0
                || operation is IConversionOperation conversion
                    && ConversionMayThrow(conversion)
                || operation is IObjectCreationOperation or IAnonymousObjectCreationOperation;
            _throwingOperations.Add(operation, throwing);
            return throwing;
        }

        private static bool ArithmeticMayThrow(BinaryOperatorKind kind, bool isChecked, ITypeSymbol? type)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];
            var integral = IsIntegral(type);
            var decimalType = type?.SpecialType == SpecialType.System_Decimal;
            return kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && (integral || decimalType)
                || kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply
                    && (decimalType || isChecked && integral);
        }

        private bool IsBoxing(IConversionOperation operation)
            => operation.Operand.Type is { } source && operation.Type is { } destination
                && ((CSharpCompilation)semanticModel.Compilation).ClassifyConversion(source, destination).IsBoxing;

        private bool ConversionMayThrow(IConversionOperation operation)
        {
            if (operation.OperatorMethod is not null || IsBoxing(operation))
                return true;
            if (operation.IsTryCast || operation.ConstantValue.HasValue || operation.Conversion.IsIdentity)
                return false;
            if (operation.Operand.Type is not { } source || operation.Type is not { } destination)
                return false;
            // Classify the types, not the cast syntax: an explicit cast can still use
            // a safe widening or reference conversion.
            var conversion = ((CSharpCompilation)semanticModel.Compilation).ClassifyConversion(source, destination);
            if (conversion.IsImplicit)
                return conversion.IsDynamic;
            if (conversion.IsUnboxing || conversion.IsReference || conversion.IsDynamic)
                return true;
            if (source is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableSource)
            {
                if (destination is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
                    return true;
                source = nullableSource.TypeArguments[0];
            }
            if (destination is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableDestination)
                destination = nullableDestination.TypeArguments[0];
            return (operation.IsChecked || IsCheckedContext(operation.Syntax)) && IsIntegral(destination)
                || source.SpecialType == SpecialType.System_Decimal
                || destination.SpecialType == SpecialType.System_Decimal;
        }

        private bool IsCheckedContext(SyntaxNode syntax)
        {
            // Roslyn can omit IsChecked on lifted nullable conversions. The nearest
            // checked/unchecked syntax overrides the compilation's overflow setting.
            foreach (var ancestor in syntax.AncestorsAndSelf())
            {
                if (ancestor is CheckedExpressionSyntax expression)
                    return expression.Keyword.IsKind(SyntaxKind.CheckedKeyword);
                if (ancestor is CheckedStatementSyntax statement)
                    return statement.Keyword.IsKind(SyntaxKind.CheckedKeyword);
            }
            return ((CSharpCompilation)semanticModel.Compilation).Options.CheckOverflow;
        }

        private static bool IsIntegral(ITypeSymbol? type)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];
            return type?.TypeKind == TypeKind.Enum
                || type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
        }

        private static IOperation? UnwrapException(IOperation? operation)
        {
            while (operation is IConversionOperation { OperatorMethod: null } conversion
                   && (conversion.IsImplicit || conversion.Conversion.IsIdentity))
                operation = conversion.Operand;
            return operation;
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
                cancellationToken.ThrowIfCancellationRequested();
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
            var possible = certain || exceptionType is null or IDynamicTypeSymbol
                || catchType is ITypeParameterSymbol
                || !exactType && (exceptionType is ITypeParameterSymbol parameter
                    ? SatisfiesClassConstraints(catchType, parameter)
                    : HasBaseType(catchType, exceptionType));
            return (possible, certain);
        }

        private static bool SatisfiesClassConstraints(ITypeSymbol? catchType, ITypeParameterSymbol parameter, int depth = 0)
        {
            // A runtime subclass can add interfaces that the catch type does not implement.
            // Only class constraints restrict which catch hierarchies can overlap.
            if (depth == 32)
                return true;
            foreach (var constraint in parameter.ConstraintTypes)
            {
                if (constraint is ITypeParameterSymbol inherited)
                {
                    if (!SatisfiesClassConstraints(catchType, inherited, depth + 1))
                        return false;
                }
                else if (constraint.TypeKind == TypeKind.Class && !HasBaseType(catchType, constraint))
                    return false;
            }
            return true;
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
