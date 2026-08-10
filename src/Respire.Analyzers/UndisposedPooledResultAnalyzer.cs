using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Respire.Analyzers;

/// <summary>
/// RESP001: a <c>RespireResult</c> or <c>RespireLease</c> awaited into a local and then dropped
/// without disposal leaks its pooled buffer.
/// </summary>
/// <remarks>
/// The analysis is deliberately conservative: it only reports a local whose every use is a
/// non-owning read (<c>result.AsString()</c>, <c>result[0]</c>, …). The moment the value is passed
/// on, returned, assigned, or captured, ownership belongs to code this rule cannot see and it stays
/// silent. Nested results from the indexer are views over the root, are never obtained from an
/// <c>await</c>, and so are never candidates.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedPooledResultAnalyzer : DiagnosticAnalyzer
{
    internal const string ResultTypeName = "Respire.RespireResult";
    internal const string LeaseTypeName = "Respire.RespireLease";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.UndisposedPooledResult,
        title: "Pooled Respire result is never disposed",
        messageFormat: "'{0}' holds a pooled {1} that is never disposed; declare it with 'using' so the buffer returns to the pool",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "RespireResult and RespireLease are leases over pooled memory. A local that is awaited "
            + "into existence and then never disposed keeps that buffer out of the pool for good. "
            + "Declare it with a 'using' declaration (or dispose it explicitly).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var resultType = compilationStart.Compilation.GetTypeByMetadataName(ResultTypeName);
            var leaseType = compilationStart.Compilation.GetTypeByMetadataName(LeaseTypeName);
            if (resultType is null && leaseType is null)
            {
                return;
            }

            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => Analyze(nodeContext, resultType, leaseType),
                SyntaxKind.LocalDeclarationStatement);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol? resultType, INamedTypeSymbol? leaseType)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;

        // `using var result = …` / `using (…)` already own the disposal.
        if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
        {
            return;
        }

        var scope = ScopeWalker.GetEnclosingScope(declaration);
        if (scope is null)
        {
            return;
        }

        foreach (var variable in declaration.Declaration.Variables)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (variable.Initializer is null || ScopeWalker.Unwrap(variable.Initializer.Value) is not AwaitExpressionSyntax)
            {
                // Only an awaited call hands out a fresh lease; indexer views and copies do not.
                continue;
            }

            if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not ILocalSymbol local)
            {
                continue;
            }

            var pooledType = Match(local.Type, resultType) ?? Match(local.Type, leaseType);
            if (pooledType is null)
            {
                continue;
            }

            if (IsDisposedOrEscapes(context, scope, local))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), local.Name, pooledType.Name));
        }
    }

    private static INamedTypeSymbol? Match(ITypeSymbol candidate, INamedTypeSymbol? pooledType)
        => pooledType is not null && SymbolEqualityComparer.Default.Equals(candidate, pooledType) ? pooledType : null;

    /// <summary>
    /// True when the local is disposed, or when it leaves this scope in any way — both mean the
    /// rule has nothing to say.
    /// </summary>
    private static bool IsDisposedOrEscapes(SyntaxNodeAnalysisContext context, SyntaxNode scope, ILocalSymbol local)
    {
        foreach (var reference in ScopeWalker.FindReferences(scope, local, context.SemanticModel, context.CancellationToken))
        {
            if (ScopeWalker.IsNestedInLambda(reference, scope))
            {
                return true;
            }

            switch (reference.Parent)
            {
                // result.AsString(), result.Type, result.Dispose()
                case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, reference):
                    if (member.Name.Identifier.ValueText == nameof(IDisposable.Dispose))
                    {
                        return true;
                    }

                    break;

                // result[0] — a non-owning view of an element.
                case ElementAccessExpressionSyntax element when ScopeWalker.IsSame(element.Expression, reference):
                    break;

                // result?.Dispose()
                case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, reference):
                    if (conditional.WhenNotNull.DescendantNodesAndSelf().OfType<MemberBindingExpressionSyntax>()
                        .Any(binding => binding.Name.Identifier.ValueText == nameof(IDisposable.Dispose)))
                    {
                        return true;
                    }

                    break;

                // using (result) { … }
                case UsingStatementSyntax usingStatement when usingStatement.Expression is not null
                                                              && ScopeWalker.IsSame(usingStatement.Expression, reference):
                    return true;

                // Anything else — an argument, a return, an assignment — hands ownership away.
                default:
                    return true;
            }
        }

        return false;
    }
}
