using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>Opt-in migration assistance for the StackExchange.Redis IDatabase API.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StackExchangeMigrationAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Supported = new(
        DiagnosticIds.StackExchangeMigration, "Migrate database method to Respire",
        "Migrate '{0}' and its IDatabase parameter to RespireClient", DiagnosticIds.Category,
        DiagnosticSeverity.Info, isEnabledByDefault: false);

    internal static readonly DiagnosticDescriptor Manual = new(
        DiagnosticIds.StackExchangeManualMigration, "Review StackExchange.Redis migration manually",
        "'{0}' requires manual migration: {1}", DiagnosticIds.Category,
        DiagnosticSeverity.Warning, isEnabledByDefault: false);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Supported, Manual);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetOperation(invocation, context.CancellationToken) is not IInvocationOperation operation) return;
        if (MigrationPlan.IsDatabaseSelection(operation))
        {
            context.ReportDiagnostic(Diagnostic.Create(Manual, invocation.GetLocation(), operation.TargetMethod.Name,
                "configure the Respire connection and database explicitly; connection setup is never rewritten"));
            return;
        }
        if (!MigrationPlan.IsDatabaseCall(operation)) return;

        var plan = MigrationPlan.Create(invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>(), context.SemanticModel, context.CancellationToken);
        if (plan is not null)
        {
            // One diagnostic/fix per method, avoiding overlapping edits in Fix All.
            if (plan.Calls[0] == invocation)
                context.ReportDiagnostic(Diagnostic.Create(Supported, invocation.GetLocation(), plan.Method.Identifier.ValueText));
        }
        else
        {
            context.ReportDiagnostic(Diagnostic.Create(Manual, invocation.GetLocation(), operation.TargetMethod.Name,
                "the containing method, database usage, arguments or result shape is outside the supported scope"));
        }
    }
}
