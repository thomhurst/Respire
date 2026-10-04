using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>
/// Finite branch constraints over stable locals and parameters. Calls, properties, overloaded
/// operators and mutable/captured variables are deliberately unknown. The same constraints serve
/// origin selection, switch dispatch and exception filters.
/// </summary>
internal sealed class FlowConditions
{
    // Each predicate occupies one bit in the ulong known/value masks.
    private const int MaxPredicates = sizeof(ulong) * 8;
    internal const ulong UnknownPathFlag = 0;
    private readonly CancellationToken _cancellationToken;
    private readonly SyntaxNode _scope;
    private readonly Dictionary<CaptureId, IOperation> _captures = new();
    private readonly Dictionary<CaptureId, ulong> _capturedReceivers = new();
    private readonly HashSet<CaptureId> _capturedLocations = [];
    private readonly Dictionary<IOperation, ulong> _locationReceivers = new();
    private readonly HashSet<CaptureId> _ambiguousCaptures = [];
    private readonly HashSet<ISymbol> _unstable = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _relevant = new(SymbolEqualityComparer.Default);
    private readonly List<(ISymbol? Symbol, object? Constant, BinaryOperatorKind Operator)> _predicates = [];

    internal ulong ReservePathFlag()
    {
        if (_predicates.Count == MaxPredicates) return UnknownPathFlag;
        var flag = 1UL << _predicates.Count;
        // Non-variable facts share the bounded path-state masks without write invalidation.
        _predicates.Add((null, null, BinaryOperatorKind.None));
        return flag;
    }

    internal FlowConditions(ControlFlowGraph graph, BasicBlock originBlock, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _scope = graph.OriginalOperation.Syntax;
        Inspect(graph.OriginalOperation);
        foreach (var block in graph.Blocks)
        {
            foreach (var operation in block.Operations.Concat(block.BranchValue is { } value ? [value] : []))
            {
                Inspect(operation);
            }
        }
        // Earlier selections only matter when a later branch can use them in the proof.
        // Forgetting unrelated predicates merges equivalent pre-origin search states.
        CollectReachablePredicates(graph, originBlock);
    }

    private void CollectReachablePredicates(ControlFlowGraph graph, BasicBlock originBlock)
    {
        var pending = new Stack<BasicBlock>();
        var visited = new HashSet<int>();
        pending.Push(originBlock);
        while (pending.Count != 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var block = pending.Pop();
            if (!visited.Add(block.Ordinal))
                continue;
            if (block.BranchValue is { } condition)
            {
                CollectRelevant(condition);
                CollectReceiverSymbols(condition);
            }
            foreach (var operation in block.Operations)
                CollectReceiverSymbols(operation);
            AddBranch(block.FallThroughSuccessor);
            AddBranch(block.ConditionalSuccessor);
            // Exceptional successors are implicit in Roslyn's CFG. Include their
            // predicates as well as loop back-edges and normal continuations.
            for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                if (region.Kind == ControlFlowRegionKind.Try
                    && region.EnclosingRegion is { Kind: ControlFlowRegionKind.TryAndCatch or ControlFlowRegionKind.TryAndFinally } owner)
                    foreach (var handler in owner.NestedRegions)
                        if (handler != region)
                            pending.Push(graph.Blocks[handler.FirstBlockOrdinal]);
        }

        // A relevant receiver can inherit its null state through earlier local copies.
        var assignments = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
        foreach (var block in graph.Blocks)
            foreach (var operation in block.Operations.Concat(block.BranchValue is { } value ? [value] : []))
                CollectAssignments(operation);
        var relevantSymbols = new Stack<ISymbol>(_relevant);
        while (relevantSymbols.Count != 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (assignments.TryGetValue(relevantSymbols.Pop(), out var sources))
                foreach (var source in sources)
                    if (_relevant.Add(source)) relevantSymbols.Push(source);
        }

        void CollectAssignments(IOperation operation)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation) return;
            if (operation is ISimpleAssignmentOperation { IsRef: false } assignment
                && Symbol(assignment.Target) is { } target && Symbol(assignment.Value) is { } source)
            {
                if (!assignments.TryGetValue(target, out var sources)) assignments.Add(target, sources = []);
                sources.Add(source);
            }
            foreach (var child in operation.ChildOperations) CollectAssignments(child);
        }

        void AddBranch(ControlFlowBranch? branch)
        {
            if (branch?.Destination is { } destination)
                pending.Push(destination);
            if (branch is not null)
                foreach (var finalizer in branch.FinallyRegions)
                    pending.Push(graph.Blocks[finalizer.FirstBlockOrdinal]);
        }
    }

    private void CollectRelevant(IOperation operation)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        operation = Unwrap(operation);
        if (Symbol(operation) is { } symbol) _relevant.Add(symbol);
        foreach (var child in operation.ChildOperations) CollectRelevant(child);
    }

    private void CollectReceiverSymbols(IOperation operation)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation) return;
        if (operation is IBinaryOperation { IsLifted: true } or ICompoundAssignmentOperation { IsLifted: true }
            or IUnaryOperation { IsLifted: true } or IIncrementOrDecrementOperation { IsLifted: true }
            || ImplicitExceptionClassifier.IsDelegateCombination(operation))
            foreach (var operand in operation.ChildOperations) CollectRelevant(operand);
        var receiver = operation switch
        {
            IInvocationOperation invocation => invocation.Instance,
            IMemberReferenceOperation member => member.Instance,
            IArrayElementReferenceOperation array => array.ArrayReference,
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder } binary => binary.RightOperand,
            ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder } compound => compound.Value,
            IConversionOperation { Operand.Type.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } conversion => conversion.Operand,
            IConversionOperation { Operand.Type.IsReferenceType: true } conversion => conversion.Operand,
            _ => null,
        };
        if (receiver is not null && Symbol(receiver) is { } symbol)
            _relevant.Add(symbol);
        foreach (var child in operation.ChildOperations)
            CollectReceiverSymbols(child);
    }

    private void Inspect(IOperation operation, bool nested = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        nested |= operation is IAnonymousFunctionOperation or ILocalFunctionOperation;
        switch (operation)
        {
            case IFlowCaptureOperation capture:
                if (_captures.ContainsKey(capture.Id))
                {
                    _captures.Remove(capture.Id);
                    _ambiguousCaptures.Add(capture.Id);
                }
                else if (!_ambiguousCaptures.Contains(capture.Id))
                    _captures.Add(capture.Id, capture.Value);
                break;
            case IAssignmentOperation assignment:
                if (assignment.Target is IFlowCaptureReferenceOperation location)
                    _capturedLocations.Add(location.Id);
                // A declaration initializes the local once per execution. Locals declared in
                // loops are excluded below as well: the next iteration can choose a new value.
                if (nested && assignment.Target is not ILocalReferenceOperation { IsDeclaration: true })
                    Invalidate(assignment.Target);
                if (assignment is ISimpleAssignmentOperation { IsRef: true })
                    Invalidate(assignment.Value);
                break;
            case IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None, Initializer: { } initializer }:
                Invalidate(initializer.Value);
                break;
            case IIncrementOrDecrementOperation increment:
                if (nested) Invalidate(increment.Target);
                break;
            case IArgumentOperation { Parameter.RefKind: not RefKind.None } argument:
                Invalidate(argument.Value);
                break;
            case IDynamicInvocationOperation invocation:
                for (var index = 0; index < invocation.Arguments.Length; index++)
                    if (invocation.GetArgumentRefKind(index) is RefKind.Ref or RefKind.Out or RefKind.In)
                        Invalidate(invocation.Arguments[index]);
                break;
            case IDynamicObjectCreationOperation creation:
                for (var index = 0; index < creation.Arguments.Length; index++)
                    if (creation.GetArgumentRefKind(index) is RefKind.Ref or RefKind.Out or RefKind.In)
                        Invalidate(creation.Arguments[index]);
                break;
            case IAddressOfOperation address:
                Invalidate(address.Reference);
                break;
        }

        foreach (var child in operation.ChildOperations)
            Inspect(child, nested);
    }

    internal void ForgetOwnWrite(IOperation operation, ref ulong known, ref ulong values)
    {
        if (operation is IAssignmentOperation assignment)
        {
            var simple = assignment is ISimpleAssignmentOperation { IsRef: false };
            var nonNull = simple && IsKnownNonNull(assignment.Value, known, values);
            var isNull = simple && IsKnownNull(assignment.Value, known, values);
            Forget(assignment.Target, ref known, ref values);
            if ((nonNull || isNull)
                && assignment.Target.Type is { IsReferenceType: true } or { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
                && Symbol(assignment.Target) is { } symbol && _relevant.Contains(symbol)
                && !_unstable.Contains(symbol)
                && symbol is not ILocalSymbol { RefKind: not RefKind.None }
                && symbol is not IParameterSymbol { RefKind: not RefKind.None })
            {
                // Snapshot the completed RHS before forgetting the target (including self-copies).
                // Subsequent writes invalidate the copied fact normally.
                var index = PredicateIndex(symbol, null, BinaryOperatorKind.Equals);
                if (index >= 0)
                {
                    known |= 1UL << index;
                    values = isNull ? values | (1UL << index) : values & ~(1UL << index);
                }
            }
        }
        else if (operation is IIncrementOrDecrementOperation increment)
            Forget(increment.Target, ref known, ref values);
    }

    internal void Forget(IOperation target, ref ulong known, ref ulong values)
    {
        if (Symbol(target) is { } symbol)
            for (var index = 0; index < _predicates.Count; index++)
                if (SymbolEqualityComparer.Default.Equals(_predicates[index].Symbol, symbol)
                    || _predicates[index].Constant is ISymbol other && SymbolEqualityComparer.Default.Equals(other, symbol))
                {
                    var mask = ~(1UL << index);
                    known &= mask;
                    values &= mask;
                }
        if (target is ITupleOperation or IDeclarationExpressionOperation)
            foreach (var child in target.ChildOperations)
                Forget(child, ref known, ref values);
    }

    private void Invalidate(IOperation operation)
    {
        if (Symbol(operation) is { } symbol)
            _unstable.Add(symbol);
        // Array indexes and member receivers identify storage; taking that storage's
        // address does not expose the locals used to calculate its location.
        else if (operation is ITupleOperation or IDeclarationExpressionOperation)
            foreach (var child in operation.ChildOperations)
                Invalidate(child);
    }

    private IOperation Unwrap(IOperation operation)
    {
        // Captures are compiler temporaries, never arbitrary user expressions to re-evaluate.
        for (var depth = 0; depth < 32; depth++)
        {
            if (operation is IConversionOperation conversion
                && (conversion.Conversion.IsIdentity
                    || conversion is { IsImplicit: true, Conversion: { IsReference: true, IsUserDefined: false } }))
                operation = conversion.Operand;
            else if (operation is IFlowCaptureReferenceOperation capture
                     && _captures.TryGetValue(capture.Id, out var value))
                operation = value;
            else
                break;
        }
        return operation;
    }

    private ISymbol? Symbol(IOperation operation) => Unwrap(operation) switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        _ => null,
    };

    internal bool IsConstructedReceiver(IOperation operation)
        => Unwrap(operation) switch
        {
            IObjectCreationOperation { Type.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, Arguments.Length: 0 } => false,
            IObjectCreationOperation or IArrayCreationOperation or IWithOperation { CloneMethod: not null }
            // The CFG lowers a record copy to the compiler-generated clone method.
                or IInvocationOperation { TargetMethod: { Name: "<Clone>$", ContainingType.IsRecord: true } } => true,
            _ => false,
        };

    internal IOperation ResolveCapturedTarget(IOperation operation) => Unwrap(operation);

    internal bool IsKnownNonZero(IOperation operation, ulong known, ulong values)
    {
        while (operation is IConversionOperation { Conversion.IsIdentity: true } conversion)
            operation = conversion.Operand;
        if (operation is IFlowCaptureReferenceOperation
            || Symbol(operation) is not { } symbol || _unstable.Contains(symbol)) return false;
        for (var index = 0; index < _predicates.Count; index++)
        {
            var predicate = _predicates[index];
            var mask = 1UL << index;
            if ((known & mask) == 0 || !SymbolEqualityComparer.Default.Equals(predicate.Symbol, symbol)
                || predicate.Constant is not (sbyte or byte or short or ushort or int or uint or long or ulong or decimal)) continue;
            var constant = Convert.ToDecimal(predicate.Constant);
            bool? zeroSatisfiesPredicate = predicate.Operator switch
            {
                BinaryOperatorKind.Equals => constant == 0,
                BinaryOperatorKind.NotEquals => constant != 0,
                BinaryOperatorKind.LessThan => 0 < constant,
                BinaryOperatorKind.LessThanOrEqual => 0 <= constant,
                BinaryOperatorKind.GreaterThan => 0 > constant,
                BinaryOperatorKind.GreaterThanOrEqual => 0 >= constant,
                _ => null,
            };
            if (zeroSatisfiesPredicate is { } zeroMatches && zeroMatches != ((values & mask) != 0))
                return true;
        }
        return false;
    }

    internal bool IsKnownType(IOperation operation, ITypeSymbol? destination, Compilation compilation, ulong known, ulong values)
    {
        while (operation is IConversionOperation { OperatorMethod: null } conversion
            && (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference))
            operation = conversion.Operand;
        if (destination is null || operation is IFlowCaptureReferenceOperation
            || Symbol(operation) is not { } symbol || _unstable.Contains(symbol)) return false;
        for (var index = 0; index < _predicates.Count; index++)
        {
            if (_predicates[index] is not { Constant: ITypeSymbol type, Operator: BinaryOperatorKind.None } predicate
                || !SymbolEqualityComparer.Default.Equals(predicate.Symbol, symbol)
                || (known & values & (1UL << index)) == 0) continue;
            var conversion = compilation.ClassifyCommonConversion(type, destination);
            if (conversion.IsIdentity || conversion.IsImplicit && conversion.IsReference) return true;
        }
        return false;
    }

    internal bool IsKnownNull(IOperation operation, ulong known, ulong values)
    {
        while (operation is IConversionOperation { OperatorMethod: null } conversion
            && (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference))
            operation = conversion.Operand;
        if (operation.ConstantValue is { HasValue: true, Value: null }
            || operation is IDefaultValueOperation { Type.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
                or IObjectCreationOperation { Type.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, Arguments.Length: 0 })
            return true;
        // Do not re-read a captured local after intervening writes.
        if (operation is IFlowCaptureReferenceOperation
            || Symbol(operation) is not { } symbol || _unstable.Contains(symbol)) return false;
        for (var index = 0; index < _predicates.Count; index++)
            if (_predicates[index] is { Constant: null, Operator: BinaryOperatorKind.Equals } predicate
                && SymbolEqualityComparer.Default.Equals(predicate.Symbol, symbol)
                && (known & values & (1UL << index)) != 0)
                return true;
        return false;
    }

    internal bool IsKnownNonNull(IOperation operation, ulong known, ulong values)
    {
        if (_locationReceivers.TryGetValue(operation, out var receiverFlag))
            return receiverFlag != UnknownPathFlag && (known & values & receiverFlag) != 0;
        while (operation is IConversionOperation { OperatorMethod: null } conversion
            && (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference))
            operation = conversion.Operand;
        // A compiler temporary retains the receiver evaluated before later argument/RHS writes.
        if (operation is IFlowCaptureReferenceOperation capture)
            return _capturedReceivers.TryGetValue(capture.Id, out var flag)
                && flag != UnknownPathFlag && (known & values & flag) != 0;
        if (operation.ConstantValue is { HasValue: true, Value: not null } || IsConstructedReceiver(operation)) return true;
        if (Symbol(operation) is not { } symbol || _unstable.Contains(symbol)) return false;
        for (var index = 0; index < _predicates.Count; index++)
        {
            var predicate = _predicates[index];
            var mask = 1UL << index;
            if ((known & mask) == 0 || !SymbolEqualityComparer.Default.Equals(predicate.Symbol, symbol)) continue;
            if (predicate is { Constant: null, Operator: BinaryOperatorKind.Equals } && (values & mask) == 0
                || predicate is { Constant: ITypeSymbol, Operator: BinaryOperatorKind.None } && (values & mask) != 0)
                return true;
        }
        return false;
    }

    internal void RecordCapturedReceiver(IFlowCaptureOperation capture, ref ulong known, ref ulong values)
    {
        var receiver = IsCapturedLocation(capture) ? capture.Value switch
        {
            IMemberReferenceOperation member => member.Instance,
            IArrayElementReferenceOperation array => array.ArrayReference,
            _ => null,
        } : capture.Value;
        if (receiver?.Type?.IsReferenceType != true) return;
        // Re-evaluating a location in a loop must read the current source facts.
        _locationReceivers.Remove(receiver);
        var nonNull = IsKnownNonNull(receiver, known, values);
        if (!_capturedReceivers.TryGetValue(capture.Id, out var flag))
            _capturedReceivers.Add(capture.Id, flag = ReservePathFlag());
        if (IsCapturedLocation(capture)) _locationReceivers[receiver] = flag;
        known |= flag;
        values = nonNull ? values | flag : values & ~flag;
    }

    internal bool IsCapturedLocation(IFlowCaptureOperation capture)
        => _capturedLocations.Contains(capture.Id)
            && capture.Value is IPropertyReferenceOperation { Property.ReturnsByRef: false, Property.ReturnsByRefReadonly: false }
                or IFieldReferenceOperation or IArrayElementReferenceOperation;

    internal bool Constrain(IOperation? condition, bool expected, ref ulong known, ref ulong values)
    {
        if (condition is null)
            return true;
        condition = Unwrap(condition);
        if (condition.ConstantValue is { HasValue: true, Value: bool constant })
            return constant == expected;
        // Do not prove cleanup by making a loop's exit impossible. Repeated
        // acquisitions can overwrite owned values even when the guard is stable.
        foreach (var ancestor in condition.Syntax.AncestorsAndSelf())
        {
            var loopCondition = ancestor switch
            {
                WhileStatementSyntax loop => loop.Condition,
                DoStatementSyntax loop => loop.Condition,
                ForStatementSyntax loop => loop.Condition,
                _ => null,
            };
            if (loopCondition?.Span.Contains(condition.Syntax.Span) == true)
                return true;
        }
        if (condition is IIsPatternOperation { Pattern: IDiscardPatternOperation })
            return expected;
        if (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } not)
            return Constrain(not.Operand, !expected, ref known, ref values);

        IOperation operand = condition;
        object? comparison = true;
        var comparisonOperator = BinaryOperatorKind.Equals;
        if (condition is IIsTypeOperation isType)
        {
            operand = isType.ValueOperand;
            comparison = isType.TypeOperand;
            comparisonOperator = BinaryOperatorKind.None;
        }
        else if (condition is IIsNullOperation isNull)
        {
            operand = isNull.Operand;
            comparison = null;
        }
        else if (condition is IBinaryOperation { OperatorMethod: null,
                     LeftOperand.Type.SpecialType: SpecialType.System_Boolean,
                     RightOperand.Type.SpecialType: SpecialType.System_Boolean } boolean
                 && boolean.OperatorKind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr)
        {
            var left = Unwrap(boolean.LeftOperand);
            var right = Unwrap(boolean.RightOperand);
            var booleanConstant = left.ConstantValue is { HasValue: true, Value: bool leftValue } ? leftValue
                : right.ConstantValue is { HasValue: true, Value: bool rightValue } ? rightValue : (bool?)null;
            if (booleanConstant is { } constantValue)
            {
                var constantOnLeft = left.ConstantValue.HasValue;
                if (boolean.OperatorKind == BinaryOperatorKind.And && !constantValue) return !expected;
                if (boolean.OperatorKind == BinaryOperatorKind.Or && constantValue) return expected;
                return Constrain(constantOnLeft ? right : left,
                    boolean.OperatorKind == BinaryOperatorKind.ExclusiveOr ? expected != constantValue : expected,
                    ref known, ref values);
            }
            if (Symbol(right) is not { } rightSymbol || !CanTrackSymbol(rightSymbol)) return true;
            operand = left;
            comparison = rightSymbol;
            comparisonOperator = boolean.OperatorKind;
        }
        else if (condition is IBinaryOperation { OperatorMethod: null } binary
            && binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
                or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
                or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
        {
            comparisonOperator = binary.OperatorKind;
            var left = Unwrap(binary.LeftOperand);
            var right = Unwrap(binary.RightOperand);
            if (left.Type?.TypeKind == TypeKind.Dynamic || right.Type?.TypeKind == TypeKind.Dynamic)
                return true;
            if (right.ConstantValue.HasValue)
                (operand, comparison) = (left, right.ConstantValue.Value);
            else if (left.ConstantValue.HasValue)
            {
                (operand, comparison) = (right, left.ConstantValue.Value);
                comparisonOperator = comparisonOperator switch
                {
                    BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
                    BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
                    BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
                    BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
                    _ => comparisonOperator,
                };
            }
            else if (IsPrimitiveComparisonOperand(left.Type)
                && SymbolEqualityComparer.Default.Equals(left.Type, right.Type)
                && Symbol(right) is { } rightSymbol && CanTrackSymbol(rightSymbol))
                (operand, comparison) = (left, rightSymbol);
            else
                return true;
            if (comparisonOperator == BinaryOperatorKind.NotEquals)
            {
                comparisonOperator = BinaryOperatorKind.Equals;
                expected = !expected;
            }
        }
        else if (condition is IIsPatternOperation isPattern)
        {
            var pattern = isPattern.Pattern;
            while (pattern is INegatedPatternOperation negated)
            {
                expected = !expected;
                pattern = negated.Pattern;
            }
            if (pattern is IDiscardPatternOperation)
                return expected;
            operand = isPattern.Value;
            if (pattern is IConstantPatternOperation { Value.ConstantValue.HasValue: true } constantPattern)
                comparison = constantPattern.Value.ConstantValue.Value;
            else if (pattern is IRecursivePatternOperation { DeconstructionSubpatterns.Length: 0, PropertySubpatterns.Length: 0 }
                && pattern.Syntax is RecursivePatternSyntax { Type: null })
            {
                comparison = null;
                expected = !expected;
            }
            else if (pattern is IRelationalPatternOperation { Value.ConstantValue.HasValue: true } relationalPattern)
            {
                comparison = relationalPattern.Value.ConstantValue.Value;
                comparisonOperator = relationalPattern.OperatorKind;
            }
            else if (pattern is ITypePatternOperation typePattern)
            {
                comparison = typePattern.MatchedType;
                comparisonOperator = BinaryOperatorKind.None;
            }
            else if (pattern is IDeclarationPatternOperation { MatchesNull: true })
                return expected;
            else if (pattern is IDeclarationPatternOperation { MatchesNull: false } declarationPattern)
            {
                comparison = declarationPattern.MatchedType;
                comparisonOperator = BinaryOperatorKind.None;
            }
            else
                return true;
        }
        else if (condition.Type?.SpecialType != SpecialType.System_Boolean)
            return true;

        // Only total orders have complementary relational operators. Nullable operands
        // and floating-point NaN can make both comparisons false.
        if (operand.Type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_Decimal)
        {
            if (comparisonOperator == BinaryOperatorKind.LessThanOrEqual)
            {
                comparisonOperator = BinaryOperatorKind.GreaterThan;
                expected = !expected;
            }
            else if (comparisonOperator == BinaryOperatorKind.GreaterThanOrEqual)
            {
                comparisonOperator = BinaryOperatorKind.LessThan;
                expected = !expected;
            }
        }

        if (comparison is false && operand.Type?.SpecialType == SpecialType.System_Boolean)
        {
            comparison = true;
            expected = !expected;
        }
        if (comparison is true && comparisonOperator == BinaryOperatorKind.Equals
            && operand is IPropertyReferenceOperation
                { Property: { Name: "HasValue", ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }, Instance: { } nullable })
        {
            operand = nullable;
            comparison = null;
            expected = !expected;
        }
        var symbol = Symbol(operand);
        if (symbol is null || !CanTrackSymbol(symbol)) return true;

        var index = PredicateIndex(symbol, comparison, comparisonOperator);
        if (index < 0) return true;
        var mask = 1UL << index;
        if ((known & mask) != 0)
            return ((values & mask) != 0) == expected;
        known |= mask;
        if (expected)
            values |= mask;
        return true;
    }

    private static bool IsPrimitiveComparisonOperand(ITypeSymbol? type)
        => type?.TypeKind == TypeKind.Enum || type?.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Char or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
            or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_Single
            or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_String;

    private bool CanTrackSymbol(ISymbol symbol)
    {
        if (!_relevant.Contains(symbol) || _unstable.Contains(symbol)
            || symbol is IParameterSymbol { RefKind: not RefKind.None }
            || symbol is ILocalSymbol { RefKind: not RefKind.None })
            return false;

        // Captures and loop-local declarations can be changed without an ordinary assignment
        // in this graph. Do not correlate their values across executions.
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (!_scope.Span.Contains(reference.Span))
                return false;
            var syntax = reference.GetSyntax(_cancellationToken);
            if (syntax.Ancestors().Any(static node => node is
                    ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax))
                return false;
        }
        return true;
    }

    private int PredicateIndex(ISymbol symbol, object? comparison, BinaryOperatorKind comparisonOperator)
    {
        var index = _predicates.FindIndex(predicate =>
            SymbolEqualityComparer.Default.Equals(predicate.Symbol, symbol)
            && (predicate.Constant is ISymbol leftType && comparison is ISymbol rightType
                ? SymbolEqualityComparer.Default.Equals(leftType, rightType)
                : Equals(predicate.Constant, comparison))
            && predicate.Operator == comparisonOperator);
        if (index < 0)
        {
            if (_predicates.Count == MaxPredicates)
                return -1;
            index = _predicates.Count;
            _predicates.Add((symbol, comparison, comparisonOperator));
        }
        return index;
    }
}
