using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>Classifies operation failures without traversing control-flow paths or dispatching catches.</summary>
internal sealed class ImplicitExceptionClassifier(
    SemanticModel semanticModel, FlowConditions conditions, CancellationToken cancellationToken)
{
    private readonly Dictionary<IOperation, bool> _throwingOperations = new();

    internal bool MayThrow(IOperation operation)
    {
        if (operation is IInvocationOperation
            { TargetMethod: { Name: "GetValueOrDefault", ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } })
            return false;
        if (operation is IPropertyReferenceOperation
            { Property: { Name: "HasValue", ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } })
            return false;
        if (_throwingOperations.TryGetValue(operation, out var cached))
            return cached;
        var throwing = operation is IDeconstructionAssignmentOperation or IInvocationOperation or IFunctionPointerInvocationOperation or IAwaitOperation or IPropertyReferenceOperation
            or IDynamicInvocationOperation or IArrayElementReferenceOperation or ITypeParameterObjectCreationOperation
            or IEventAssignmentOperation or IArrayCreationOperation
            or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation
            or IDynamicObjectCreationOperation
            or IWithOperation { CloneMethod: not null }
            or IRecursivePatternOperation { DeconstructSymbol: not null }
            or IInterpolatedStringOperation { ConstantValue.HasValue: false }
            or IInterpolationOperation or IDelegateCreationOperation
            or IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String, ConstantValue.HasValue: false }
            or ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String }
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
                && !conditions.IsConstructedReceiver(receiver)
                // A member binding is evaluated only on the non-null conditional-access path.
                && operation.Syntax is not MemberBindingExpressionSyntax
            || operation is IFieldReferenceOperation { Field: { IsStatic: true, IsConst: false } field }
                && field.ContainingType.StaticConstructors.Length != 0
            || operation is IConversionOperation conversion
                && ConversionMayThrow(conversion)
            || operation is IAnonymousObjectCreationOperation
            || operation is IObjectCreationOperation creation
                && !(creation.Type is INamedTypeSymbol { IsValueType: true, StaticConstructors.Length: 0 }
                    && creation.Constructor is null or { IsImplicitlyDeclared: true });
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

    internal static bool IsFrameworkLength(IOperation operation)
        => operation is IPropertyReferenceOperation { Property.Name: "Length" or "LongLength",
            Property.ContainingType.SpecialType: SpecialType.System_Array }
            or IPropertyReferenceOperation { Property.Name: "Length", Property.ContainingType.SpecialType: SpecialType.System_String };

    internal static string? KnownPropertyException(IOperation operation)
        => operation is IPropertyReferenceOperation
            { Property: { Name: "Value", ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } }
            ? "System.InvalidOperationException" : null;

    internal bool IsTrivialReferenceConstruction(IOperation operation)
    {
        if (operation is not IObjectCreationOperation { Type: INamedTypeSymbol type } creation) return false;
        if (type.SpecialType == SpecialType.System_Object) return true;
        if (creation.Constructor is not { IsImplicitlyDeclared: true }
            || type.BaseType?.SpecialType != SpecialType.System_Object
            || type.StaticConstructors.Length != 0 || type.DeclaringSyntaxReferences.Length == 0) return false;
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration) return false;
            foreach (var member in declaration.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member is BaseFieldDeclarationSyntax field && field.Declaration.Variables.Any(static variable => variable.Initializer is not null)
                    || member is PropertyDeclarationSyntax { Initializer: not null }) return false;
            }
        }
        return true;
    }

    internal bool IsNonNegativeLength(IOperation operation)
    {
        operation = conditions.ResolveCapturedTarget(operation);
        return IsFrameworkLength(operation) || operation.ConstantValue is { HasValue: true,
            Value: byte or ushort or uint or ulong or sbyte and >= 0 or short and >= 0 or int and >= 0 or long and >= 0 };
    }

    internal static (bool Overflow, bool DivideByZero)? ArithmeticExceptions(IOperation operation)
    {
        var kind = operation switch
        {
            IBinaryOperation { OperatorMethod: null } binary => binary.OperatorKind,
            ICompoundAssignmentOperation { OperatorMethod: null, Target: ILocalReferenceOperation or IParameterReferenceOperation } compound => compound.OperatorKind,
            IIncrementOrDecrementOperation { OperatorMethod: null, Target: ILocalReferenceOperation or IParameterReferenceOperation } => BinaryOperatorKind.Add,
            IUnaryOperation { OperatorMethod: null, OperatorKind: UnaryOperatorKind.Minus } => BinaryOperatorKind.Subtract,
            _ => BinaryOperatorKind.None,
        };
        var type = operation.Type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        if (!IsIntegral(type) && type?.SpecialType != SpecialType.System_Decimal) return null;
        if (kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply)
            return (true, false);
        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder)
        {
            var overflow = type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_IntPtr
                || kind == BinaryOperatorKind.Divide && (type?.SpecialType == SpecialType.System_Decimal
                    || type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Int16
                        && operation is ICompoundAssignmentOperation { IsChecked: true, OutConversion.IsIdentity: false });
            var divisor = operation is IBinaryOperation binary ? binary.RightOperand
                : ((ICompoundAssignmentOperation)operation).Value;
            if (IsIntegral(type) && !IntegralDivisionCanOverflow(divisor)) overflow = false;
            var constantDivisor = UnwrapDivisor(divisor).ConstantValue;
            var divideByZero = !constantDivisor.HasValue || constantDivisor.Value is null
                || (constantDivisor.Value is char character ? character == 0 : Convert.ToDouble(constantDivisor.Value) == 0);
            return (overflow, divideByZero);
        }
        return null;
    }

    private static bool IntegralDivisionCanOverflow(IOperation divisor)
    {
        divisor = UnwrapDivisor(divisor);
        var type = divisor.Type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        if (type?.SpecialType is SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_Char
            or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_UIntPtr) return false;
        if (IsIntegral(type) && divisor.ConstantValue is { HasValue: true, Value: { } value })
            return Convert.ToDecimal(value) == -1;
        return true;
    }

    private static IOperation UnwrapDivisor(IOperation divisor)
    {
        // Numeric promotion and wrapping the same value in Nullable<T> preserve zero.
        while (divisor is IConversionOperation { Conversion.IsUserDefined: false, OperatorMethod: null } conversion
            && (conversion.IsImplicit || conversion.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                && SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], conversion.Operand.Type)))
            divisor = conversion.Operand;
        return divisor;
    }

    internal bool DelegateCanDereferenceNull(IDelegateCreationOperation operation, ulong known, ulong values)
        => operation.Target is IMethodReferenceOperation { Method.IsStatic: false, Instance: { } receiver }
            && CanDereferenceNull(receiver) && !conditions.IsKnownNonNull(receiver, known, values);

    internal bool HasValidConstantIndexes(IArrayElementReferenceOperation operation)
    {
        if (conditions.ResolveCapturedTarget(operation.ArrayReference) is not IArrayCreationOperation creation
            || creation.DimensionSizes.Length != operation.Indices.Length) return false;
        for (var dimension = 0; dimension < operation.Indices.Length; dimension++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConstantIntegral(operation.Indices[dimension]) is not { } index
                || ConstantIntegral(creation.DimensionSizes[dimension]) is not { } length
                || index < 0 || index >= length) return false;
        }
        return true;
    }

    private static decimal? ConstantIntegral(IOperation operation)
        => operation.ConstantValue is { HasValue: true, Value: { } value }
            && value is sbyte or byte or short or ushort or int or uint or long or ulong or char
                ? Convert.ToDecimal(value is char character ? (int)character : value) : null;

    internal bool CanDereferenceNull(IOperation receiver)
        => receiver.Type?.IsReferenceType == true && receiver is not IInstanceReferenceOperation
            && !conditions.IsConstructedReceiver(receiver);

    internal static bool IsAllocationOnlyInterpolation(IOperation operation)
        => operation is IInterpolatedStringOperation
                || operation is IInterpolationOperation { FormatString: null } interpolation
                    && interpolation.Expression.Type?.SpecialType is SpecialType.System_String
                        or SpecialType.System_Char or SpecialType.System_Boolean
                        or SpecialType.System_SByte or SpecialType.System_Byte
                        or SpecialType.System_Int16 or SpecialType.System_UInt16
                        or SpecialType.System_Int32 or SpecialType.System_UInt32
                        or SpecialType.System_Int64 or SpecialType.System_UInt64
                        or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    internal static bool IsStringOnlyConcatenation(IOperation operation)
        => operation switch
        {
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String,
                OperatorMethod: null } binary => IsStringOrNull(binary.LeftOperand) && IsStringOrNull(binary.RightOperand),
            ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String,
                OperatorMethod: null } assignment => IsStringOrNull(assignment.Value),
            _ => false,
        };

    private static bool IsStringOrNull(IOperation operation)
        => operation.Type?.SpecialType == SpecialType.System_String
            || operation.ConstantValue is { HasValue: true, Value: null };

    internal bool IsBoxing(IConversionOperation operation)
        => operation.Operand.ConstantValue is not { HasValue: true, Value: null }
            && operation.Operand is not IDefaultValueOperation
                { Type: INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } }
            && operation.Operand.Type is { } source && operation.Type is { } destination
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

    internal (bool InvalidCast, bool NullReference, bool InvalidOperation, bool Overflow)? ConversionExceptions(IConversionOperation operation)
    {
        if (operation.Operand.Type is not { } source || operation.Type is not { } destination || IsBoxing(operation))
            return null;
        var conversion = ((CSharpCompilation)semanticModel.Compilation).ClassifyConversion(source, destination);
        if (conversion.IsUserDefined || conversion.IsDynamic)
            return null;
        var nullableDestination = destination is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        if (conversion.IsUnboxing)
            return (true, !nullableDestination, false, false);
        if (conversion.IsReference)
            return (true, false, false, false);
        var nullableSource = source is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        if (nullableSource) source = ((INamedTypeSymbol)source).TypeArguments[0];
        if (nullableDestination) destination = ((INamedTypeSymbol)destination).TypeArguments[0];
        var numeric = ((CSharpCompilation)semanticModel.Compilation).ClassifyConversion(source, destination);
        var overflow = !numeric.IsImplicit && ((operation.IsChecked || IsCheckedContext(operation.Syntax)) && IsIntegral(destination)
            || source.SpecialType == SpecialType.System_Decimal && IsIntegral(destination)
            || destination.SpecialType == SpecialType.System_Decimal
                && source.SpecialType is SpecialType.System_Single or SpecialType.System_Double);
        return (false, false, nullableSource && !nullableDestination, overflow);
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
}
