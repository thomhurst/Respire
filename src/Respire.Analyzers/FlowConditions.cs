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
    private readonly List<(ISymbol Symbol, object? Constant, BinaryOperatorKind Operator)> _predicates = [];

    internal FlowConditions(ControlFlowGraph graph, int originPosition, CancellationToken cancellationToken)
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
        foreach (var block in graph.Blocks)
            if (block.BranchValue is { } condition && condition.Syntax.Span.End >= originPosition)
                CollectRelevant(condition);
    }

    private void CollectRelevant(IOperation operation)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        operation = Unwrap(operation);
        if (Symbol(operation) is { } symbol) _relevant.Add(symbol);
        foreach (var child in operation.ChildOperations) CollectRelevant(child);
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
            case IIncrementOrDecrementOperation increment when nested:
                Invalidate(increment.Target);
                break;
            case IArgumentOperation { Parameter.RefKind: not RefKind.None } argument:
                Invalidate(argument.Value);
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
            if (operation is IConversionOperation { Conversion.IsIdentity: true } conversion)
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

    internal bool Constrain(IOperation? condition, bool expected, ref ulong known, ref ulong values)
    {
        if (condition is null)
            return true;
        condition = Unwrap(condition);
        if (condition.ConstantValue is { HasValue: true, Value: bool constant })
            return constant == expected;
        if (condition is IIsPatternOperation { Pattern: IDiscardPatternOperation })
            return expected;
        if (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } not)
            return Constrain(not.Operand, !expected, ref known, ref values);

        IOperation operand = condition;
        object? comparison = true;
        var comparisonOperator = BinaryOperatorKind.Equals;
        if (condition is IIsNullOperation isNull)
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
        else if (condition is IIsPatternOperation { Pattern: IConstantPatternOperation pattern } isPattern
                 && pattern.Value.ConstantValue.HasValue)
        {
            operand = isPattern.Value;
            comparison = pattern.Value.ConstantValue.Value;
        }
        else if (condition.Type?.SpecialType != SpecialType.System_Boolean)
            return true;

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
            && Equals(predicate.Constant, comparison) && predicate.Operator == comparisonOperator);
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
