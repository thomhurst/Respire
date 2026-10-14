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
            if (await HasPotentialCallersAsync(plan.Method.Identifier.ValueText, context.Document.Project, cancellationToken).ConfigureAwait(false)) continue;
            var updated = context.Document.WithSyntaxRoot(root.ReplaceNode(plan.Method,
                plan.Rewrite().WithAdditionalAnnotations(Formatter.Annotation)));

            // Only diagnose the changed document and its partial type. The name scan protects callers
            // without forcing a semantic diagnostics pass over every dependent project.
            if (!await CompilesAsync(updated, cancellationToken).ConfigureAwait(false)) continue;
            context.RegisterCodeFix(CodeAction.Create(Title, _ => Task.FromResult(updated), Title), diagnostic);
        }
    }

    private static async Task<bool> HasPotentialCallersAsync(string methodName, Project project, CancellationToken cancellationToken)
    {
        // References to the current symbol miss calls that bind elsewhere before migration:
        // using-static imports and inherited overloads can become ambiguous or change binding.
        // Conservatively reject any use of the name, without diagnosing entire compilations.
        // Declaration identifiers are tokens, not SimpleNameSyntax, so unused overloads remain eligible.
        var solution = project.Solution;
        var projectIds = solution.GetProjectDependencyGraph()
            .GetProjectsThatTransitivelyDependOnThisProject(project.Id).Prepend(project.Id);
        foreach (var projectId in projectIds)
        {
            var candidateProject = solution.GetProject(projectId)!;
            if (candidateProject.Language != LanguageNames.CSharp) return true;
            var documents = candidateProject.Documents.Concat(
                await candidateProject.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false));
            foreach (var document in documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null) return true;
                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (name.Identifier.ValueText == methodName) return true;
                }
            }
        }
        return false;
    }

    private static async Task<bool> CompilesAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null) return false;
        // A duplicate signature can be diagnosed on the unchanged half of a partial type.
        var trees = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Select(type => model.GetDeclaredSymbol(type, cancellationToken))
            .Where(symbol => symbol is not null)
            .SelectMany(symbol => symbol!.DeclaringSyntaxReferences.Select(reference => reference.SyntaxTree))
            .Append(model.SyntaxTree).Distinct();
        foreach (var tree in trees)
            if (model.Compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken)
                .Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
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
            // Individually valid signature changes can collide when combined across methods.
            var documents = solution.GetChanges(fixAllContext.Solution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments());
            foreach (var id in documents)
                if (!await CompilesAsync(solution.GetDocument(id)!, fixAllContext.CancellationToken).ConfigureAwait(false)) return null;
            return CodeAction.Create(Title, _ => Task.FromResult(solution), Title);
        }
    }
}
