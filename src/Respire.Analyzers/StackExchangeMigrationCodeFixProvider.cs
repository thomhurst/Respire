using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Respire.Analyzers;

/// <summary>Migrates a self-contained database method and validates its callers before offering a fix.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(StackExchangeMigrationCodeFixProvider)), Shared]
public sealed class StackExchangeMigrationCodeFixProvider : CodeFixProvider
{
    internal const string Title = "Migrate method and IDatabase parameter to RespireClient";

    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticIds.StackExchangeMigration);
    public override FixAllProvider GetFixAllProvider() => new ValidatedFixAllProvider();

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var cancellationToken = context.CancellationToken;
        var root = await context.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            var method = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();
            var plan = MigrationPlan.Create(method, model, cancellationToken);
            if (plan is null) continue;
            var updated = context.Document.WithSyntaxRoot(root.ReplaceNode(plan.Method,
                plan.Rewrite().WithAdditionalAnnotations(Formatter.Annotation)));

            // Do not leave known callers, overloads, or method-group conversions broken. A
            // missing Respire reference also prevents an action. No dependency is installed.
            var solution = updated.Project.Solution;
            var projectIds = solution.GetProjectDependencyGraph()
                .GetProjectsThatTransitivelyDependOnThisProject(updated.Project.Id).Append(updated.Project.Id);
            if (!await CompilesAsync(solution, projectIds, cancellationToken).ConfigureAwait(false)) continue;
            context.RegisterCodeFix(CodeAction.Create(Title, _ => Task.FromResult(updated), Title), diagnostic);
        }
    }

    private static async Task<bool> CompilesAsync(Solution solution, IEnumerable<ProjectId> projectIds, CancellationToken cancellationToken)
    {
        foreach (var projectId in projectIds.Distinct())
        {
            var compilation = await solution.GetProject(projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null || compilation.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        }
        return true;
    }

    private sealed class ValidatedFixAllProvider : FixAllProvider
    {
        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            var action = await WellKnownFixAllProviders.BatchFixer.GetFixAsync(fixAllContext).ConfigureAwait(false);
            if (action is null) return null;
            var operations = await action.GetOperationsAsync(fixAllContext.CancellationToken).ConfigureAwait(false);
            var solution = operations.OfType<ApplyChangesOperation>().SingleOrDefault()?.ChangedSolution;
            if (solution is null) return null;
            var graph = solution.GetProjectDependencyGraph();
            var projects = solution.GetChanges(fixAllContext.Solution).GetProjectChanges()
                .SelectMany(p => graph.GetProjectsThatTransitivelyDependOnThisProject(p.ProjectId).Append(p.ProjectId));
            // Individually valid signature changes can collide when combined across methods.
            if (!await CompilesAsync(solution, projects, fixAllContext.CancellationToken).ConfigureAwait(false)) return null;
            return CodeAction.Create(Title, _ => Task.FromResult(solution), Title);
        }
    }
}
