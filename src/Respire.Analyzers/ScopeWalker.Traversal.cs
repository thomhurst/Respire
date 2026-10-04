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
        private readonly Dictionary<(int Block, int Position), List<(int TriggerBlock, ulong Flag)>> _transferGuards = new();
        private readonly Dictionary<SyntaxNode, ulong> _transferTriggers = new();
        private readonly HashSet<(int Block, int Position)> _unconditionalBarriers = [];
        private ulong _transferFlags;
        private readonly Dictionary<INamedTypeSymbol, ulong> _initializedTypes = new(SymbolEqualityComparer.Default);
        // Interned continuations keep each finally's return destination in the search state.
        private readonly List<(int Block, int Next, ControlFlowRegion? Finally)> _continuations = [(-1, 0, null)];
        private readonly Dictionary<(int Block, int Next, ControlFlowRegion Finally), int> _continuationIds = new();
        private readonly List<CatchDispatch?> _dispatches = [null];
        private readonly Dictionary<(int Block, int Continuation, bool NullPath, bool Implicit, bool AllocationOnly, string? ExceptionType), int> _dispatchIds = new();
        private ImplicitExceptionClassifier? _exceptionClassifier;
        private ImplicitExceptionClassifier Exceptions => _exceptionClassifier ??= new(semanticModel, _conditions, cancellationToken);
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
                var (block, position) = FindBarrierLocation(barrier);
                if (block is null)
                {
                    continue;
                }

                if (!_barrierPositions.TryGetValue(block.Ordinal, out var positions))
                {
                    positions = [];
                    _barrierPositions.Add(block.Ordinal, positions);
                }

                positions.Add(position);
            }

            foreach (var positions in _barrierPositions.Values)
                positions.Sort();
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

                var firstBarrier = FindFirstBarrier(block, entryPosition, started, known & values);
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

        private (BasicBlock? Block, int Position) FindBarrierLocation(SyntaxNode barrier)
        {
            var scope = origin is null ? graph.OriginalOperation.Syntax : GetEnclosingScope(origin);
            var capture = barrier.Ancestors().TakeWhile(node => node != scope)
                .OfType<AnonymousFunctionExpressionSyntax>().LastOrDefault();
            if (capture is not null)
            {
                foreach (var block in graph.Blocks)
                    foreach (var operation in block.Operations.Concat(block.BranchValue is { } branch ? [branch] : []))
                        if (FindDelegateCreation(operation, capture) is { } creation)
                            return (block, creation.Syntax.Span.End);
            }
            var expression = barrier is ExpressionSyntax value ? GetOutermostTransparentExpression(value) : null;
            var wrapped = false;
            while (expression is not null)
            {
                ExpressionSyntax? wrapper = expression.Parent switch
                {
                    ArgumentSyntax { Parent: TupleExpressionSyntax tuple } => tuple,
                    ExpressionElementSyntax { Parent: CollectionExpressionSyntax collection } => collection,
                    InitializerExpressionSyntax arrayInitializer when arrayInitializer.IsKind(SyntaxKind.ArrayInitializerExpression) => arrayInitializer,
                    ArrayCreationExpressionSyntax array when array.Initializer == expression => array,
                    ImplicitArrayCreationExpressionSyntax array when array.Initializer == expression => array,
                    CastExpressionSyntax cast when cast.Expression == expression => cast,
                    ConditionalExpressionSyntax conditional when conditional.Condition != expression => conditional,
                    BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression) => coalesce,
                    SwitchExpressionArmSyntax { Parent: SwitchExpressionSyntax selection } arm when arm.Expression == expression => selection,
                    AssignmentExpressionSyntax discarded when discarded.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && discarded.Right == expression
                        && semanticModel.GetOperation(discarded, cancellationToken) is IAssignmentOperation assigned
                        && IsDiscardedReference(assigned.Target, assigned.Value, barrier)
                        && GetOutermostTransparentExpression(discarded).Parent is not ExpressionStatementSyntax => discarded,
                    _ => null,
                };
                if (wrapper is null) break;
                expression = GetOutermostTransparentExpression(wrapper);
                wrapped = true;
            }
            var call = expression?.Parent is ArgumentSyntax { Parent: ArgumentListSyntax arguments }
                ? arguments.Parent : expression;
            var operatorTransfer = expression?.Parent is { } operatorSyntax
                && semanticModel.GetOperation(operatorSyntax, cancellationToken) is
                    IBinaryOperation { OperatorMethod: not null } or IUnaryOperation { OperatorMethod: not null };
            if (operatorTransfer) call = expression!.Parent;
            var collectionTransfer = expression?.Parent is InitializerExpressionSyntax collectionElement
                && (collectionElement.IsKind(SyntaxKind.ComplexElementInitializerExpression)
                    || collectionElement.IsKind(SyntaxKind.CollectionInitializerExpression));
            if (expression?.Parent is InitializerExpressionSyntax complexElement
                && complexElement.IsKind(SyntaxKind.ComplexElementInitializerExpression))
                call = complexElement;
            var returnTransfer = expression?.Parent is ReturnStatementSyntax;
            var initializerTransfer = expression?.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax };
            if (initializerTransfer)
                call = expression!.Parent!.Parent;
            var assignmentTransfer = false;
            var indexerTransfer = expression?.Parent is ArgumentSyntax { Parent: BracketedArgumentListSyntax };
            if (expression?.Parent is ArgumentSyntax { Parent: BracketedArgumentListSyntax { Parent: ElementAccessExpressionSyntax indexer } })
            {
                call = indexer;
                if (indexer.Parent is AssignmentExpressionSyntax indexedAssignment
                    && indexedAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && indexedAssignment.Left == indexer)
                {
                    call = indexedAssignment;
                    assignmentTransfer = true;
                }
            }
            if (expression is not null && (wrapped || Unwrap(expression) is IdentifierNameSyntax)
                && expression.Parent is AssignmentExpressionSyntax assignment
                && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Right == expression)
            {
                if (semanticModel.GetOperation(assignment, cancellationToken) is IAssignmentOperation assignmentOperation
                    && IsDiscardedReference(assignmentOperation.Target, assignmentOperation.Value, barrier))
                    return (null, 0);
                call = assignment;
                assignmentTransfer = true;
            }
            if (call is not null && (assignmentTransfer || returnTransfer || initializerTransfer || indexerTransfer || collectionTransfer || operatorTransfer
                || call is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
            {
                foreach (var block in graph.Blocks)
                    foreach (var operation in block.Operations.Concat(block.BranchValue is { } branch ? [branch] : []))
                        if (returnTransfer ? operation == block.BranchValue && operation.Syntax == call : ContainsCall(operation, call))
                        {
                            // Returns and local initializers transfer ownership only after
                            // the complete expression, including its final conversion.
                            var position = returnTransfer || initializerTransfer ? call.Span.End : TransferPosition(call);
                            if (wrapped)
                            {
                                var reference = barrier is ExpressionSyntax barrierExpression ? Unwrap(barrierExpression) : barrier;
                                var triggerBlock = graph.Blocks.FirstOrDefault(candidate =>
                                    candidate.Operations.Concat(candidate.BranchValue is { } branchValue ? [branchValue] : [])
                                        .Any(candidateOperation => ContainsReference(candidateOperation, reference)));
                                var flag = _conditions.ReservePathFlag();
                                // Barriers stop paths. Omitting an unproven barrier only adds
                                // reachable paths, so exhaustion can retain a warning, never hide one.
                                if (triggerBlock is null || flag == FlowConditions.UnknownPathFlag) return (null, 0);
                                _transferFlags |= flag;
                                _transferTriggers[reference] = _transferTriggers.TryGetValue(reference, out var existing) ? existing | flag : flag;
                                if (!_transferGuards.TryGetValue((block.Ordinal, position), out var guards))
                                    _transferGuards.Add((block.Ordinal, position), guards = []);
                                guards.Add((triggerBlock.Ordinal, flag));
                            }
                            else _unconditionalBarriers.Add((block.Ordinal, position));
                            return (block, position);
                        }
            }
            return (FindBlock(graph, barrier), barrier.SpanStart);

            static bool ContainsCall(IOperation operation, SyntaxNode call)
            {
                if (operation.Syntax == call && operation is IInvocationOperation or IFunctionPointerInvocationOperation or IDynamicInvocationOperation
                    or IObjectCreationOperation or IDynamicObjectCreationOperation or ISimpleAssignmentOperation or IDeconstructionAssignmentOperation
                    or IPropertyReferenceOperation or IDynamicIndexerAccessOperation
                    or IBinaryOperation { OperatorMethod: not null } or IUnaryOperation { OperatorMethod: not null })
                    return true;
                return operation.ChildOperations.Any(child => ContainsCall(child, call));
            }

            static bool ContainsReference(IOperation operation, SyntaxNode reference)
                => operation.Syntax == reference && operation is ILocalReferenceOperation or IParameterReferenceOperation
                    || operation.ChildOperations.Any(child => ContainsReference(child, reference));
        }

        private static IDelegateCreationOperation? FindDelegateCreation(IOperation operation, SyntaxNode capture)
        {
            if (operation is IDelegateCreationOperation creation && creation.Target.Syntax == capture)
                return creation;
            foreach (var child in operation.ChildOperations)
                if (FindDelegateCreation(child, capture) is { } found) return found;
            return null;
        }

        private bool IsDiscardedReference(IOperation target, IOperation value, SyntaxNode reference)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!value.Syntax.Span.Contains(reference.Span)) return false;
            if (target is IDiscardOperation) return true;
            while (value is IConversionOperation { OperatorMethod: null } conversion)
                value = conversion.Operand;
            if (target is ITupleOperation targets && value is ITupleOperation sources
                && targets.Elements.Length == sources.Elements.Length)
                for (var index = 0; index < targets.Elements.Length; index++)
                    if (IsDiscardedReference(targets.Elements[index], sources.Elements[index], reference)) return true;
            return false;
        }

        private static int TransferPosition(SyntaxNode operation) => operation switch
        {
            BaseObjectCreationExpressionSyntax { ArgumentList: { } arguments } => arguments.CloseParenToken.SpanStart,
            // Operators have no closing call token after the last operand. Include that
            // operand's complete evaluation, then exclude the accepting operator body.
            BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => operation.Span.End,
            _ => operation.Span.End - 1,
        };

        private void EnqueueIteratorDisposal(BasicBlock block, int entryPosition, int firstBarrier,
            bool started, ulong known, ulong values)
        {
            // An iterator can be disposed at any suspension. Its finally regions still
            // run, but ordinary statements after yield return need not execute.
            foreach (var operation in block.Operations)
            {
                if (operation.Kind == OperationKind.YieldReturn
                    && operation.Syntax.SpanStart > entryPosition
                    && operation.Syntax.Span.End <= firstBarrier)
                {
                    var unwind = CollectFinallyRegions(block.EnclosingRegion);
                    Enqueue(graph.Blocks[graph.Blocks.Length - 1], unwind, 0, started, known, values, 0);
                }
            }
        }

        private int FindFirstBarrier(BasicBlock block, int entryPosition, bool started, ulong activeTransfers)
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

            for (; index < positions.Count; index++)
                if (_unconditionalBarriers.Contains((block.Ordinal, positions[index]))
                    || !_transferGuards.TryGetValue((block.Ordinal, positions[index]), out var guards)
                    || guards.Any(guard => guard.TriggerBlock == block.Ordinal || (activeTransfers & guard.Flag) != 0))
                    return positions[index];
            return int.MaxValue;
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
            int continuation, bool started, int dispatch, ref ulong known, ref ulong values, bool deconstructionStore = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation or INameOfOperation
                || operation.Syntax.SpanStart >= firstBarrier)
                return;
            if (operation is ILocalReferenceOperation or IParameterReferenceOperation
                && _transferTriggers.TryGetValue(operation.Syntax, out var transferFlag))
            {
                known |= transferFlag;
                values |= transferFlag;
            }

            // Evaluate children before their parent's write. Each exception sees only writes
            // that have already executed, including writes in earlier call arguments.
            var exceptionSource = operation;
            var deconstructionStoresHandled = false;
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
            else if (operation is IDeconstructionAssignmentOperation deconstruction)
            {
                // Deconstruction evaluates target-location side effects before the RHS,
                // but performs the target stores only after the RHS and conversions.
                VisitDeconstructionLocations(deconstruction.Target, block, entryPosition, firstBarrier, continuation,
                    started, dispatch, ref known, ref values);
                Visit(deconstruction.Value, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
                if ((operation.Syntax.Span.End <= firstBarrier || TransferPosition(operation.Syntax) == firstBarrier)
                    && HasMatchingDeconstructionShape(deconstruction.Target, deconstruction.Value))
                {
                    VisitDeconstructionStores(deconstruction.Target, deconstruction.Value, block, entryPosition, firstBarrier,
                        continuation, started, dispatch, ref known, ref values);
                    deconstructionStoresHandled = true;
                }
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
            else if (operation is IFlowCaptureOperation location && _conditions.IsCapturedLocation(location))
            {
                // Capturing storage evaluates its receiver/indexes, not a property getter.
                foreach (var child in location.Value.ChildOperations)
                    Visit(child, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
            }
            else if (!deconstructionStore)
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
            // Receiver checks, allocation, type initialization and dynamic binding precede
            // callee entry, where responsibility transfers. Callee-body failures are excluded.
            var transferFailure = TransferPosition(operation.Syntax) == firstBarrier
                ? GetTransferFailure(operation, known, values) : TransferFailure.None;
            if (!deconstructionStoresHandled && operation.Syntax.SpanStart > entryPosition
                // Arguments and receivers inside the origin run before acquisition completes.
                && !(entryPosition == startPosition && origin?.Span.Contains(operation.Syntax.Span) == true)
                && !(exceptionSource is IFieldReferenceOperation { Field.IsStatic: false, Instance: { } fieldReceiver }
                    && _conditions.IsKnownNonNull(fieldReceiver, known, values))
                && !(exceptionSource is IFieldReferenceOperation { Field.IsStatic: true }
                    && IsTypeInitialized(exceptionSource, known, values))
                && !(ImplicitExceptionClassifier.IsFrameworkLength(exceptionSource)
                    && exceptionSource is IPropertyReferenceOperation { Instance: { } lengthReceiver }
                    && _conditions.IsKnownNonNull(lengthReceiver, known, values))
                && !(exceptionSource is IPropertyReferenceOperation
                    { Property: { Name: "Value", ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }, Instance: { } nullableReceiver }
                    && _conditions.IsKnownNonNull(nullableReceiver, known, values))
                && (operation.Syntax.Span.End <= firstBarrier
                    && !(operation.Syntax.Span.End == firstBarrier
                        && operation is IBinaryOperation { OperatorMethod: not null } or IUnaryOperation { OperatorMethod: not null })
                    && Exceptions.MayThrow(exceptionSource) || transferFailure != TransferFailure.None))
            {
                if (dispatch != 0)
                    // The runtime treats a throwing filter as a rejected filter.
                    Dispatch(_dispatches[dispatch]!.Next, started, known, values);
                else if (block.FallThroughSuccessor is { } successor)
                {
                    // Index expressions have their own exception paths. Array access itself
                    // only checks the receiver, bounds, and (for reference stores) covariance.
                    if (exceptionSource is IArrayElementReferenceOperation arrayAccess)
                    {
                        if (!_conditions.IsKnownNonNull(arrayAccess.ArrayReference, known, values))
                            Dispatch(GetDispatch(successor, continuation, implicitException: true, nullPath: true), started, known, values);
                        if (!Exceptions.HasValidConstantIndexes(arrayAccess))
                            Dispatch(GetDispatch(successor, continuation, implicitException: true,
                                implicitExceptionType: "System.IndexOutOfRangeException"), started, known, values);
                        if (arrayAccess.Type?.IsValueType != true
                            && !HasExactArrayElementType(arrayAccess)
                            && (deconstructionStore || operation is IAssignmentOperation
                                || operation.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }))
                            Dispatch(GetDispatch(successor, continuation, implicitException: true,
                                implicitExceptionType: "System.ArrayTypeMismatchException"), started, known, values);
                    }
                    else if (ImplicitExceptionClassifier.ArithmeticExceptions(exceptionSource) is { } arithmetic)
                    {
                        if (arithmetic.Overflow)
                            Dispatch(GetDispatch(successor, continuation, implicitException: true,
                                implicitExceptionType: "System.OverflowException"), started, known, values);
                        if (arithmetic.DivideByZero)
                            Dispatch(GetDispatch(successor, continuation, implicitException: true,
                                implicitExceptionType: "System.DivideByZeroException"), started, known, values);
                    }
                    else if (exceptionSource is IConversionOperation conversion && Exceptions.ConversionExceptions(conversion) is { } conversionExceptions)
                    {
                        if (conversionExceptions.InvalidCast)
                            Dispatch(GetDispatch(successor, continuation, implicitException: true,
                                implicitExceptionType: "System.InvalidCastException"), started, known, values);
                        if (conversionExceptions.NullReference)
                            Dispatch(GetDispatch(successor, continuation, implicitException: true, nullPath: true), started, known, values);
                        if (conversionExceptions.InvalidOperation)
                            Dispatch(GetDispatch(successor, continuation, implicitException: true,
                                implicitExceptionType: "System.InvalidOperationException"), started, known, values);
                        if (conversionExceptions.Overflow)
                            Dispatch(GetDispatch(successor, continuation, implicitException: true,
                                implicitExceptionType: "System.OverflowException"), started, known, values);
                    }
                    else Dispatch(GetDispatch(successor, continuation, implicitException: true,
                        implicitExceptionType: transferFailure == TransferFailure.TypeInitialization
                            || exceptionSource is IFieldReferenceOperation { Field.IsStatic: true }
                            ? "System.TypeInitializationException" : ImplicitExceptionClassifier.KnownPropertyException(exceptionSource),
                        nullPath: transferFailure == TransferFailure.NullReceiver
                            || exceptionSource is IFieldReferenceOperation { Field.IsStatic: false }
                            || ImplicitExceptionClassifier.IsFrameworkLength(exceptionSource),
                        allocationOnly: transferFailure == TransferFailure.Allocation || arrayAllocation
                            || exceptionSource is IArrayCreationOperation
                            || Exceptions.IsTrivialReferenceConstruction(exceptionSource)
                            || exceptionSource is IAnonymousObjectCreationOperation
                            || exceptionSource is IConversionOperation boxing && Exceptions.IsBoxing(boxing)
                            || ImplicitExceptionClassifier.IsStringOnlyConcatenation(exceptionSource)
                            || ImplicitExceptionClassifier.IsAllocationOnlyInterpolation(exceptionSource)
                            || exceptionSource is IDelegateCreationOperation
                            || ScopeExitAnalysis.GetKnownExactExceptionType(semanticModel.Compilation, exceptionSource) is not null),
                        started, known, values);
                    if (exceptionSource is IDelegateCreationOperation delegateCreation && Exceptions.DelegateCanDereferenceNull(delegateCreation, known, values))
                        Dispatch(GetDispatch(successor, continuation, implicitException: true, nullPath: true), started, known, values);
                    if (transferFailure == TransferFailure.Allocation
                        && exceptionSource is IObjectCreationOperation { Type: INamedTypeSymbol { StaticConstructors.Length: > 0 } }
                        && !IsTypeInitialized(exceptionSource, known, values))
                        Dispatch(GetDispatch(successor, continuation, implicitException: true,
                            implicitExceptionType: "System.TypeInitializationException"), started, known, values);
                    if (exceptionSource is IArrayCreationOperation arrayCreation
                        && arrayCreation.DimensionSizes.Any(size => !Exceptions.IsNonNegativeLength(size)))
                        Dispatch(GetDispatch(successor, continuation, implicitException: true,
                            implicitExceptionType: "System.OverflowException"), started, known, values);
                }
            }
            // Only the normal continuation proves initialization succeeded. Keep this fact
            // across writes and later exceptions; a successful type initializer never reruns.
            if (InitializationType(exceptionSource) is { } initializedType)
            {
                if (!_initializedTypes.TryGetValue(initializedType, out var flag))
                {
                    flag = _conditions.ReservePathFlag();
                    _initializedTypes.Add(initializedType, flag);
                }
                known |= flag;
                values |= flag;
            }
            // Construction/allocation can fail before any initializer runs.
            if (initializer is not null)
                Visit(initializer, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
            if (arrayAllocation)
                foreach (var child in operation.ChildOperations)
                    Visit(child, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
            _conditions.ForgetOwnWrite(operation, ref known, ref values);
            if (operation is IFlowCaptureOperation captured)
                _conditions.RecordCapturedReceiver(captured, ref known, ref values);
        }

        private bool HasExactArrayElementType(IArrayElementReferenceOperation access)
            => access.Type is INamedTypeSymbol { IsSealed: true }
                || _conditions.ResolveCapturedTarget(access.ArrayReference) is IArrayCreationOperation
                    { Type: IArrayTypeSymbol array }
                    && SymbolEqualityComparer.Default.Equals(array.ElementType, access.Type);

        private enum TransferFailure { None, NullReceiver, Allocation, TypeInitialization, Unknown }

        private bool IsTypeInitialized(IOperation operation, ulong known, ulong values)
            => InitializationType(operation) is { } type && _initializedTypes.TryGetValue(type, out var flag)
                && flag != FlowConditions.UnknownPathFlag && (known & values & flag) != 0;

        private static INamedTypeSymbol? InitializationType(IOperation operation)
        {
            var type = operation switch
            {
                IFieldReferenceOperation { Field: { IsStatic: true, IsConst: false } field } => field.ContainingType,
                IPropertyReferenceOperation { Property.IsStatic: true } property => property.Property.ContainingType,
                IInvocationOperation { TargetMethod.IsStatic: true } invocation => invocation.TargetMethod.ContainingType,
                IBinaryOperation { OperatorMethod: { } binaryOperator } => binaryOperator.ContainingType,
                IUnaryOperation { OperatorMethod: { } unaryOperator } => unaryOperator.ContainingType,
                IObjectCreationOperation { Type: INamedTypeSymbol createdType } => createdType,
                _ => null,
            };
            // With beforefieldinit, a method or constructor need not trigger initialization.
            return type is not null && (operation is IFieldReferenceOperation && type.StaticConstructors.Length > 0
                || type.StaticConstructors.Any(static constructor => !constructor.IsImplicitlyDeclared)) ? type : null;
        }

        private void VisitDeconstructionLocations(IOperation target, BasicBlock block, int entryPosition, int firstBarrier,
            int continuation, bool started, int dispatch, ref ulong known, ref ulong values)
        {
            foreach (var child in target.ChildOperations)
                if (target is ITupleOperation)
                    VisitDeconstructionLocations(child, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
                else
                    Visit(child, block, entryPosition, firstBarrier, continuation, started, dispatch, ref known, ref values);
        }

        private bool HasMatchingDeconstructionShape(IOperation target, IOperation value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target = _conditions.ResolveCapturedTarget(target);
            value = _conditions.ResolveCapturedTarget(value);
            if (target is not ITupleOperation targets) return true;
            if (value is ITupleOperation sources)
                return targets.Elements.Length == sources.Elements.Length
                    && targets.Elements.Select((element, index) => HasMatchingDeconstructionShape(element, sources.Elements[index])).All(static matches => matches);
            // A tuple local already contains its elements. Identical tuple types need no
            // element conversion or user-defined Deconstruct call before the stores.
            if (value is IConversionOperation conversion)
            {
                if (conversion.Conversion.IsUserDefined) return false;
                value = conversion.Operand;
            }
            return value.Type is INamedTypeSymbol { IsTupleType: true } tupleType
                && MatchesTupleElementTypes(targets, tupleType);
        }

        private bool MatchesTupleElementTypes(ITupleOperation targets, INamedTypeSymbol source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targets.Elements.Length != source.TupleElements.Length) return false;
            for (var index = 0; index < targets.Elements.Length; index++)
            {
                var target = _conditions.ResolveCapturedTarget(targets.Elements[index]);
                var elementType = source.TupleElements[index].Type;
                if (target is ITupleOperation nested)
                {
                    if (elementType is not INamedTypeSymbol { IsTupleType: true } nestedType
                        || !MatchesTupleElementTypes(nested, nestedType)) return false;
                }
                else if (!SymbolEqualityComparer.Default.Equals(target.Type, elementType)) return false;
            }
            return true;
        }

        private bool VisitDeconstructionStores(IOperation target, IOperation? value, BasicBlock block, int entryPosition, int firstBarrier,
            int continuation, bool started, int dispatch, ref ulong known, ref ulong values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target = _conditions.ResolveCapturedTarget(target);
            if (value is not null) value = _conditions.ResolveCapturedTarget(value);
            if (target is ITupleOperation targets)
            {
                var sources = value as ITupleOperation;
                for (var index = 0; index < targets.Elements.Length; index++)
                    if (VisitDeconstructionStores(targets.Elements[index], sources?.Elements[index], block, entryPosition,
                        firstBarrier, continuation, started, dispatch, ref known, ref values)) return true;
                return false;
            }
            var activeTransfers = known & values;
            var transferred = value is not null && _transferTriggers.Any(trigger => value.Syntax.Span.Contains(trigger.Key.Span)
                && (activeTransfers & trigger.Value) != 0);
            // Receivers and indexes were evaluated before the RHS; only the store runs now.
            // At the owning target, model pre-entry failures but not the accepting setter's body.
            Visit(target, block, entryPosition, transferred ? TransferPosition(target.Syntax) : firstBarrier,
                continuation, started, dispatch, ref known, ref values, deconstructionStore: true);
            _conditions.Forget(target, ref known, ref values);
            return transferred;
        }

        private TransferFailure GetTransferFailure(IOperation operation, ulong known, ulong values)
        {
            if (operation is ISimpleAssignmentOperation assignment)
                operation = _conditions.ResolveCapturedTarget(assignment.Target);
            return operation switch
            {
                IObjectCreationOperation { Type.IsReferenceType: true } => TransferFailure.Allocation,
                IObjectCreationOperation { Type: INamedTypeSymbol { StaticConstructors.Length: > 0 } }
                    when !IsTypeInitialized(operation, known, values) => TransferFailure.TypeInitialization,
                IDeconstructionAssignmentOperation or IDynamicInvocationOperation or IDynamicObjectCreationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation
                    or IArrayElementReferenceOperation => TransferFailure.Unknown,
                IInvocationOperation { Instance: { } receiver } when Exceptions.CanDereferenceNull(receiver) && !_conditions.IsKnownNonNull(receiver, known, values) => TransferFailure.NullReceiver,
                IPropertyReferenceOperation { Instance: { } receiver } when Exceptions.CanDereferenceNull(receiver) && !_conditions.IsKnownNonNull(receiver, known, values) => TransferFailure.NullReceiver,
                IFieldReferenceOperation { Instance: { } receiver } when Exceptions.CanDereferenceNull(receiver) && !_conditions.IsKnownNonNull(receiver, known, values) => TransferFailure.NullReceiver,
                IPropertyReferenceOperation { Property: { IsStatic: true, ContainingType.StaticConstructors.Length: > 0 } }
                    or IFieldReferenceOperation { Field: { IsStatic: true, ContainingType.StaticConstructors.Length: > 0 } }
                    or IInvocationOperation { TargetMethod: { IsStatic: true, ContainingType.StaticConstructors.Length: > 0 } }
                    or IBinaryOperation { OperatorMethod.ContainingType.StaticConstructors.Length: > 0 }
                    or IUnaryOperation { OperatorMethod.ContainingType.StaticConstructors.Length: > 0 }
                    when !IsTypeInitialized(operation, known, values) => TransferFailure.TypeInitialization,
                _ => TransferFailure.None,
            };
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
                        && !_conditions.IsKnownNonNull(exception, known, values)
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
            bool implicitException = false, bool allocationOnly = false, string? implicitExceptionType = null)
        {
            var key = (branch.Source.Ordinal, continuation, nullPath, implicitException, allocationOnly, implicitExceptionType);
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
                exceptionType = nullPath
                    ? semanticModel.Compilation.GetTypeByMetadataName("System.NullReferenceException")
                    : allocationOnly ? semanticModel.Compilation.GetTypeByMetadataName("System.OutOfMemoryException")
                    : implicitExceptionType is not null ? semanticModel.Compilation.GetTypeByMetadataName(implicitExceptionType) : null;
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
            // An exception abandons argument evaluation before any pending call can accept ownership.
            known &= ~_transferFlags;
            values &= ~_transferFlags;
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
