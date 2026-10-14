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
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeOutsideMethod, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        var calls = new List<(InvocationExpressionSyntax Syntax, IInvocationOperation Operation)>();
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.SemanticModel.GetOperation(invocation, context.CancellationToken) is not IInvocationOperation operation) continue;
            if (MigrationPlan.IsDatabaseSelection(operation)) ReportSelection(context, invocation, operation);
            else if (MigrationPlan.IsDatabaseCall(operation)) calls.Add((invocation, operation));
        }
        if (calls.Count == 0) return;
        var plan = MigrationPlan.Create(method, context.SemanticModel, context.CancellationToken);
        if (plan is not null)
            context.ReportDiagnostic(Diagnostic.Create(Supported, plan.Calls[0].GetLocation(), method.Identifier.ValueText));
        else
            foreach (var call in calls) ReportManual(context, call.Syntax, call.Operation);
    }

    private static void AnalyzeOutsideMethod(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not null) return;
        if (context.SemanticModel.GetOperation(invocation, context.CancellationToken) is not IInvocationOperation operation) return;
        if (MigrationPlan.IsDatabaseSelection(operation)) ReportSelection(context, invocation, operation);
        else if (MigrationPlan.IsDatabaseCall(operation)) ReportManual(context, invocation, operation);
    }

    private static void ReportSelection(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, IInvocationOperation operation)
        => context.ReportDiagnostic(Diagnostic.Create(Manual, invocation.GetLocation(), operation.TargetMethod.Name,
            "configure the Respire connection and database explicitly; connection setup is never rewritten"));

    private static void ReportManual(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, IInvocationOperation operation)
        => context.ReportDiagnostic(Diagnostic.Create(Manual, invocation.GetLocation(), operation.TargetMethod.Name,
            "the containing method, database usage, arguments or result shape is outside the supported scope"));
}
