using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>Flags explicit operation-scoped ownership and client creation in loops.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ShortLivedClientAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.ShortLivedClient, "Share a long-lived Respire client",
        "Reuse a shared RespireClient instead of creating one for each operation",
        DiagnosticIds.Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "RespireClient multiplexes connections and should normally live for the application lifetime.",
        helpLinkUri: "https://thomhurst.github.io/Respire/docs/diagnostics/RESP010");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (!call.TargetMethod.IsStatic || call.TargetMethod.Name is not ("Create" or "ConnectAsync" or "ConnectAnyAsync")
            || call.TargetMethod.ContainingType.ToDisplayString() != "Respire.RespireClient") return;

        // Limit the lifetime heuristic to explicit using ownership or repeated construction.
        // Returning a client from a factory or storing it in a field is not operation-scoped.
        SyntaxNode? scope = null;
        var inLoop = false;
        var inUsing = false;
        foreach (var ancestor in call.Syntax.Ancestors())
        {
            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax)
            {
                scope = ancestor;
                break;
            }
            if (ancestor is WhileStatementSyntax or DoStatementSyntax
                || ancestor is CommonForEachStatementSyntax each && each.Statement.Span.Contains(call.Syntax.Span)
                || ancestor is ForStatementSyntax repeat
                    && repeat.Declaration?.Span.Contains(call.Syntax.Span) != true
                    && !repeat.Initializers.Any(initializer => initializer.Span.Contains(call.Syntax.Span)))
                inLoop = true;
            if (ancestor is LocalDeclarationStatementSyntax local && local.UsingKeyword.RawKind != 0)
                inUsing = true;
            if (ancestor is UsingStatementSyntax usingStatement
                && (usingStatement.Declaration?.Span.Contains(call.Syntax.Span) == true
                    || usingStatement.Expression?.Span.Contains(call.Syntax.Span) == true)) inUsing = true;
        }

        var entryPoint = context.Compilation.GetEntryPoint(context.CancellationToken);
        var inEntryPoint = scope is MethodDeclarationSyntax method
            && call.SemanticModel is { } model
            && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(method, context.CancellationToken), entryPoint);
        if (!inLoop && !inUsing && !inEntryPoint
            && scope is MethodDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            inUsing = HasSubsequentLocalOwnership(call, context.CancellationToken);
        if (inLoop || (inUsing && scope is MethodDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
                && !inEntryPoint))
            context.ReportDiagnostic(Diagnostic.Create(Rule, call.Syntax.GetLocation()));
    }

    private static bool HasSubsequentLocalOwnership(IInvocationOperation call, System.Threading.CancellationToken cancellationToken)
    {
        // Follow only a creation result, including awaited/configured connections, into
        // a single local initializer. Arbitrary calls and conditional values may replace it.
        IOperation value = call;
        while (value.Parent is IAwaitOperation or IConversionOperation or IParenthesizedOperation
            || value.Parent is IInvocationOperation configured
                && configured.TargetMethod.Name == "ConfigureAwait" && configured.Instance == value)
            value = value.Parent;
        if (value.Parent is not IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }
            || declarator.Syntax.Parent?.Parent is not LocalDeclarationStatementSyntax declaration
            || declaration.Declaration.Variables.Count != 1
            || declaration.Parent is not BlockSyntax block
            || call.SemanticModel is not { } model) return false;

        var first = block.Statements.IndexOf(declaration) + 1;
        for (var i = first; i < block.Statements.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statement = block.Statements[i];
            var ownsLocal = statement switch
            {
                UsingStatementSyntax owner => IsLocal(owner.Expression)
                    || owner.Declaration?.Variables.Any(variable => IsLocal(variable.Initializer?.Value)) == true,
                LocalDeclarationStatementSyntax owner when owner.UsingKeyword.RawKind != 0
                    => owner.Declaration.Variables.Any(variable => IsLocal(variable.Initializer?.Value)),
                _ => false
            };
            if (!ownsLocal) continue;

            // Include nested writes and ref/out arguments. If the value might change,
            // do not attribute subsequent disposal to the original creation.
            var flow = model.AnalyzeDataFlow(block.Statements[first], statement);
            if (flow is { Succeeded: true }
                && !flow.WrittenInside.Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, declarator.Symbol)))
                return true;
        }
        return false;

        bool IsLocal(ExpressionSyntax? expression)
        {
            if (expression is null) return false;
            var operation = model.GetOperation(expression, cancellationToken);
            while (operation is IConversionOperation conversion) operation = conversion.Operand;
            return operation is ILocalReferenceOperation reference
                && SymbolEqualityComparer.Default.Equals(reference.Local, declarator.Symbol);
        }
    }
}
