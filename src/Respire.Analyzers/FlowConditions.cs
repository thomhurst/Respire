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
    private const int MaxPredicates = 64;
    private readonly CancellationToken _cancellationToken;
    private readonly SyntaxNode _scope;
    private readonly Dictionary<CaptureId, IOperation> _captures = new();
    private readonly HashSet<CaptureId> _ambiguousCaptures = [];
    private readonly HashSet<ISymbol> _unstable = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _relevant = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _written = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, IOperation> _initializers = new(SymbolEqualityComparer.Default);
    private readonly List<(ISymbol Symbol, object? Constant, BinaryOperatorKind Operator)> _predicates = [];

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
        var receiver = operation switch
        {
            IInvocationOperation invocation => invocation.Instance,
            IMemberReferenceOperation member => member.Instance,
            _ => null,
        };
        if (receiver is not null && Symbol(receiver) is { } symbol)
            _relevant.Add(symbol);
        foreach (var child in operation.ChildOperations)
            CollectReceiverSymbols(child);
    }

    private void RecordWrite(IOperation target)
    {
        if (Symbol(target) is { } symbol) _written.Add(symbol);
        if (target is ITupleOperation or IDeclarationExpressionOperation)
            foreach (var child in target.ChildOperations) RecordWrite(child);
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
                if (assignment.Target is not ILocalReferenceOperation { IsDeclaration: true })
                    RecordWrite(assignment.Target);
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
            case IVariableDeclaratorOperation { Initializer: { } initializer } declarator:
                _initializers[declarator.Symbol] = initializer.Value;
                break;
            case IIncrementOrDecrementOperation increment:
                RecordWrite(increment.Target);
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
            Forget(assignment.Target, ref known, ref values);
        else if (operation is IIncrementOrDecrementOperation increment)
            Forget(increment.Target, ref known, ref values);
    }

    private void Forget(IOperation target, ref ulong known, ref ulong values)
    {
        if (Symbol(target) is { } symbol)
            for (var index = 0; index < _predicates.Count; index++)
                if (SymbolEqualityComparer.Default.Equals(_predicates[index].Symbol, symbol))
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
        => Unwrap(operation) is IObjectCreationOperation or IArrayCreationOperation or IWithOperation { CloneMethod: not null }
            // The CFG lowers a record copy to the compiler-generated clone method.
            or IInvocationOperation { TargetMethod: { Name: "<Clone>$", ContainingType.IsRecord: true } };

    internal IOperation ResolveCapturedTarget(IOperation operation) => Unwrap(operation);

    internal bool IsKnownNonNull(IOperation operation, ulong known, ulong values)
    {
        if (IsConstructedReceiver(operation)) return true;
        if (Symbol(operation) is not { } symbol || _unstable.Contains(symbol)) return false;
        if (!_written.Contains(symbol) && _initializers.TryGetValue(symbol, out var initializer)
            && IsConstructedReceiver(initializer)) return true;
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
        var symbol = Symbol(operand);
        if (symbol is null || !_relevant.Contains(symbol) || _unstable.Contains(symbol)
            || symbol is IParameterSymbol { RefKind: not RefKind.None }
            || symbol is ILocalSymbol { RefKind: not RefKind.None })
            return true;

        // Captures and loop-local declarations can be changed without an ordinary assignment
        // in this graph. Do not correlate their values across executions.
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (!_scope.Span.Contains(reference.Span))
                return true;
            var syntax = reference.GetSyntax(_cancellationToken);
            if (syntax.Ancestors().Any(static node => node is
                    ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax))
                return true;
        }

        var index = _predicates.FindIndex(predicate =>
            SymbolEqualityComparer.Default.Equals(predicate.Symbol, symbol)
            && (predicate.Constant is ITypeSymbol leftType && comparison is ITypeSymbol rightType
                ? SymbolEqualityComparer.Default.Equals(leftType, rightType)
                : Equals(predicate.Constant, comparison))
            && predicate.Operator == comparisonOperator);
        if (index < 0)
        {
            if (_predicates.Count == MaxPredicates)
                return true;
            index = _predicates.Count;
            _predicates.Add((symbol, comparison, comparisonOperator));
        }
        var mask = 1UL << index;
        if ((known & mask) != 0)
            return ((values & mask) != 0) == expected;
        known |= mask;
        if (expected)
            values |= mask;
        return true;
    }
}
