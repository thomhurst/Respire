using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

internal static class CompletionGuards
{
    public static bool IsSuccessfulTaskCheck(IPropertyReferenceOperation check)
    {
        return check.Property.Name == "IsCompletedSuccessfully"
            && SyncOverAsyncAnalyzer.IsTask(check.Property.ContainingType);
    }

    public static bool ChecksLocal(IPropertyReferenceOperation check, ILocalSymbol local)
    {
        return check.Instance is ILocalReferenceOperation instance
            && SymbolEqualityComparer.Default.Equals(instance.Local, local);
    }

    public static bool ProvesSuccessfulCompletion(IOperation? condition, bool value, ILocalSymbol local)
    {
        return FindTrueProperties(condition, value).Any(check => IsSuccessfulTaskCheck(check) && ChecksLocal(check, local));
    }

    public static IEnumerable<IPropertyReferenceOperation> FindChecks(
        SemanticModel model, SyntaxNode scope, SyntaxNode read, CancellationToken cancellationToken)
    {
        for (SyntaxNode current = read; current.Parent is { } parent && parent != scope; current = parent)
        {
            (ExpressionSyntax? condition, bool value) = parent switch
            {
                IfStatementSyntax statement when current == statement.Statement => (statement.Condition, true),
                IfStatementSyntax statement when current == statement.Else => (statement.Condition, false),
                ConditionalExpressionSyntax expression when current == expression.WhenTrue => (expression.Condition, true),
                ConditionalExpressionSyntax expression when current == expression.WhenFalse => (expression.Condition, false),
                BinaryExpressionSyntax binary when current == binary.Right
                    && (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression))
                    && model.GetOperation(binary, cancellationToken) is IBinaryOperation { OperatorMethod: null, Type.SpecialType: SpecialType.System_Boolean }
                    => (binary.Left, binary.IsKind(SyntaxKind.LogicalAndExpression)),
                _ => (null, false),
            };

            if (condition is null)
            {
                continue;
            }

            foreach (IPropertyReferenceOperation check in FindTrueProperties(model.GetOperation(condition, cancellationToken), value))
            {
                yield return check;
            }
        }
    }

    private static IEnumerable<IPropertyReferenceOperation> FindTrueProperties(IOperation? condition, bool value)
    {
        switch (condition)
        {
            case IPropertyReferenceOperation property when value:
                yield return property;
                break;
            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } negation:
                foreach (IPropertyReferenceOperation check in FindTrueProperties(negation.Operand, !value))
                {
                    yield return check;
                }

                break;
            case IBinaryOperation { OperatorMethod: null, Type.SpecialType: SpecialType.System_Boolean } binary when binary.OperatorKind == (value ? BinaryOperatorKind.ConditionalAnd : BinaryOperatorKind.ConditionalOr):
                foreach (IPropertyReferenceOperation check in FindTrueProperties(binary.LeftOperand, value))
                {
                    yield return check;
                }

                foreach (IPropertyReferenceOperation check in FindTrueProperties(binary.RightOperand, value))
                {
                    yield return check;
                }

                break;
        }
    }
}
