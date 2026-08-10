using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Respire.Analyzers;

/// <summary>Turns the flagged local into a <c>using</c> declaration, which is the intended shape.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UndisposedPooledResultCodeFixProvider))]
[Shared]
public sealed class UndisposedPooledResultCodeFixProvider : CodeFixProvider
{
    private const string Title = "Add 'using' so the pooled buffer is returned";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticIds.UndisposedPooledResult);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is not { } declaration)
            {
                continue;
            }

            // One declarator only: a shared `using` would change disposal for the others too.
            if (declaration.Declaration.Variables.Count != 1)
            {
                continue;
            }

            // A using declaration cannot be placed directly in a switch section (CS8647).
            if (declaration.Parent is SwitchSectionSyntax)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(AddUsingDeclaration(context.Document, root, declaration)),
                    equivalenceKey: Title),
                diagnostic);
        }
    }

    private static Document AddUsingDeclaration(Document document, SyntaxNode root, LocalDeclarationStatementSyntax declaration)
    {
        var usingKeyword = SyntaxFactory.Token(SyntaxKind.UsingKeyword)
            .WithLeadingTrivia(declaration.GetLeadingTrivia())
            .WithTrailingTrivia(SyntaxFactory.Space);

        var updated = declaration
            .WithLeadingTrivia(SyntaxFactory.TriviaList())
            .WithUsingKeyword(usingKeyword);

        return document.WithSyntaxRoot(root.ReplaceNode(declaration, updated));
    }
}
