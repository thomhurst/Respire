using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

// Migrate the complete parameter boundary, never a receiver chosen by name or an implicit
// default connection. Any other use of the database makes the method a manual migration.
internal sealed class MigrationPlan
{
    private MigrationPlan(MethodDeclarationSyntax method, ParameterSyntax parameter,
        List<InvocationExpressionSyntax> calls, Dictionary<SyntaxNode, SyntaxNode> replacements)
    {
        Method = method;
        Parameter = parameter;
        Calls = calls;
        Replacements = replacements;
    }

    internal MethodDeclarationSyntax Method { get; }
    internal ParameterSyntax Parameter { get; }
    internal List<InvocationExpressionSyntax> Calls { get; }
    private Dictionary<SyntaxNode, SyntaxNode> Replacements { get; }

    private static bool IsStackExchangeType(INamedTypeSymbol? type, string name)
        => type?.Name == name && type.ContainingNamespace.ToDisplayString() == "StackExchange.Redis"
            && type.ContainingAssembly.Name == "StackExchange.Redis";

    internal static bool IsDatabaseCall(IInvocationOperation operation)
        => IsStackExchangeType(operation.TargetMethod.ContainingType, "IDatabase")
            || IsStackExchangeType(operation.TargetMethod.ContainingType, "IDatabaseAsync");

    internal static bool IsDatabaseSelection(IInvocationOperation operation)
        => operation.TargetMethod.Name == "GetDatabase"
            && (IsStackExchangeType(operation.TargetMethod.ContainingType, "IConnectionMultiplexer")
                || IsStackExchangeType(operation.TargetMethod.ContainingType, "ConnectionMultiplexer"));

    internal static MigrationPlan? Create(MethodDeclarationSyntax? method, SemanticModel model, CancellationToken cancellationToken)
    {
        if (method is null || method.ContainsDirectives || !method.Modifiers.Any(SyntaxKind.StaticKeyword)
            || !method.Modifiers.Any(SyntaxKind.AsyncKeyword)
            || method.Modifiers.Any(SyntaxKind.PartialKeyword)
            || method.DescendantNodes().Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            || model.GetDeclaredSymbol(method, cancellationToken) is not { } symbol
            || symbol.ExplicitInterfaceImplementations.Length != 0) return null;

        var parameters = symbol.Parameters.Where(p => IsStackExchangeType(p.Type as INamedTypeSymbol, "IDatabase")).ToArray();
        if (parameters.Length != 1 || parameters[0].RefKind != RefKind.None) return null;
        var parameter = method.ParameterList.Parameters[parameters[0].Ordinal];
        if (parameter.Default is not null || parameter.AttributeLists.Count != 0) return null;

        var calls = new List<InvocationExpressionSyntax>();
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var reference in method.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference, cancellationToken).Symbol, parameters[0])) continue;
            if (reference.Parent is not MemberAccessExpressionSyntax member || member.Expression != reference
                || member.Parent is not InvocationExpressionSyntax invocation
                || model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation
                || !IsDatabaseCall(operation)
                || !TryRewrite(invocation, operation, model, cancellationToken, out var original, out var replacement)) return null;
            calls.Add(invocation);
            replacements.Add(original!, replacement!);
        }
        if (calls.Count == 0) return null;
        // Do not let a supported parameter hide diagnostics on other database receivers.
        if (method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
            model.GetOperation(i, cancellationToken) is IInvocationOperation operation
            && IsDatabaseCall(operation) && !calls.Contains(i))) return null;
        replacements.Add(parameter.Type!, SyntaxFactory.ParseTypeName("global::Respire.RespireClient").WithTriviaFrom(parameter.Type!));
        return new MigrationPlan(method, parameter, calls, replacements);
    }

    internal MethodDeclarationSyntax Rewrite()
        => Method.ReplaceNodes(Replacements.Keys, (original, _) => Replacements[original]);

    private static bool TryRewrite(InvocationExpressionSyntax invocation, IInvocationOperation operation,
        SemanticModel model, CancellationToken cancellationToken, out SyntaxNode? original, out SyntaxNode? replacement)
    {
        original = replacement = null;
        var name = operation.TargetMethod.Name;
        if (name is not ("StringGetAsync" or "StringSetAsync" or "KeyExistsAsync" or "KeyDeleteAsync")) return false;

        // Explicit flags, expiry, When, keepTtl and all array overloads require review.
        // Retain the source argument order (including named arguments) to preserve evaluation.
        foreach (var argument in operation.Arguments.Where(a => !a.IsImplicit))
        {
            if (argument.Parameter?.Name is not ("key" or "value") || argument.Syntax is not ArgumentSyntax syntax) return false;
            var type = model.GetTypeInfo(syntax.Expression, cancellationToken).Type;
            if (argument.Parameter.Name == "key")
            {
                if (type?.SpecialType != SpecialType.System_String
                    || model.GetConstantValue(syntax.Expression, cancellationToken) is { HasValue: true, Value: null }) return false;
            }
            else
            {
                // RedisValue.Null SET deletes a key; Respire rejects a null value. Only a
                // non-null constant string proves the same write semantics without flow guesses.
                if (type?.SpecialType != SpecialType.System_String
                    || model.GetConstantValue(syntax.Expression, cancellationToken) is not { HasValue: true, Value: string }) return false;
            }
        }

        ExpressionSyntax awaitedExpression = invocation;
        if (invocation.Parent is MemberAccessExpressionSyntax configureMember
            && configureMember.Expression == invocation && configureMember.Name.Identifier.ValueText == "ConfigureAwait"
            && configureMember.Parent is InvocationExpressionSyntax configureInvocation
            && model.GetSymbolInfo(configureInvocation, cancellationToken).Symbol is IMethodSymbol configureSymbol
            && configureSymbol.Parameters.Length == 1 && configureSymbol.Parameters[0].Type.SpecialType == SpecialType.System_Boolean)
            awaitedExpression = configureInvocation;
        if (awaitedExpression.Parent is not AwaitExpressionSyntax awaitExpression) return false;

        var target = name switch
        {
            "StringSetAsync" => "Strings.SetAsync",
            "KeyExistsAsync" => "Keys.ExistsAsync",
            "KeyDeleteAsync" => "Keys.DeleteAsync",
            _ => "Strings.GetStringAsync",
        };
        SyntaxNode resultNode = awaitExpression;
        if (name == "StringGetAsync")
        {
            // An explicit conversion states whether callers want nullable text or raw bytes.
            // RedisValue consumers, IsNull, implicit conversions and Task consumers stay manual.
            if (awaitExpression.Parent is not CastExpressionSyntax cast) return false;
            var castType = model.GetTypeInfo(cast.Type, cancellationToken).Type;
            if (castType is IArrayTypeSymbol array && array.ElementType.SpecialType == SpecialType.System_Byte && array.Rank == 1)
                target = "Strings.GetBytesAsync";
            else if (castType?.SpecialType != SpecialType.System_String) return false;
            resultNode = cast;
        }

        var member = (MemberAccessExpressionSyntax)invocation.Expression;
        var names = target.Split('.');
        var facet = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
            member.Expression, SyntaxFactory.IdentifierName(names[0]));
        var updatedCall = invocation.WithExpression(member.WithExpression(facet)
            .WithName(SyntaxFactory.IdentifierName(names[1]).WithTriviaFrom(member.Name)));
        if (name == "KeyDeleteAsync")
        {
            // Select the single-key overload even with a C# compiler predating params spans.
            updatedCall = updatedCall.WithArgumentList(updatedCall.ArgumentList.AddArguments(
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))
                    .WithNameColon(SyntaxFactory.NameColon("cancellationToken"))));
        }
        var updatedAwait = awaitExpression.ReplaceNode(invocation, updatedCall);
        ExpressionSyntax result = updatedAwait;
        if (name == "KeyDeleteAsync")
        {
            // StackExchange single-key DEL is bool; Respire returns the number deleted.
            // A bare comparison is not a legal expression statement, so keep discarded awaits.
            if (awaitExpression.Parent is not ExpressionStatementSyntax)
                result = SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(
                    SyntaxKind.NotEqualsExpression, updatedAwait,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))));
        }
        original = resultNode;
        replacement = result.WithTriviaFrom(resultNode);
        return true;
    }
}
