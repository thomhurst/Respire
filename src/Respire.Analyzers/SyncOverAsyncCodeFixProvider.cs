using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;

namespace Respire.Analyzers;

/// <summary>Replaces blocking consumption with await without changing public signatures.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SyncOverAsyncCodeFixProvider)), Shared]
public sealed class SyncOverAsyncCodeFixProvider : CodeFixProvider
{
    private const string Title = "Await the Respire operation";
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.SyncOverAsync);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var cancellationToken = context.CancellationToken;
        var root = await context.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var operation = model.GetOperation(node, cancellationToken);
            if (operation is null || SyncOverAsyncAnalyzer.GetAwaitable(operation)?.Syntax is not ExpressionSyntax operand) continue;
            // Timed/cancellable Wait overloads have different semantics. Never discard their arguments.
            if (operation is IInvocationOperation { TargetMethod.Name: "Wait", Arguments.Length: > 0 }) continue;

            ExpressionSyntax replacement = SyntaxFactory.AwaitExpression(operand.WithoutTrivia());
            if (node.Parent is not ExpressionStatementSyntax)
                replacement = SyntaxFactory.ParenthesizedExpression(replacement);
            replacement = replacement.WithTriviaFrom(node).WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation);
            var updatedRoot = root.ReplaceNode(node, replacement);
            // Await is valid in existing async functions and top-level statements. A Task-returning
            // method/local function can also gain async if its remaining returns still compile.
            var function = node.Ancestors().FirstOrDefault(n => n is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax);
            if (function is MethodDeclarationSyntax method && !method.Modifiers.Any(SyntaxKind.AsyncKeyword)
                && SyncOverAsyncAnalyzer.IsTask(model.GetDeclaredSymbol(method, cancellationToken)?.ReturnType))
                updatedRoot = root.ReplaceNode(method, method.ReplaceNode(node, replacement)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)).WithAdditionalAnnotations(Formatter.Annotation));
            else if (function is LocalFunctionStatementSyntax local && !local.Modifiers.Any(SyntaxKind.AsyncKeyword)
                && SyncOverAsyncAnalyzer.IsTask((model.GetDeclaredSymbol(local, cancellationToken) as IMethodSymbol)?.ReturnType))
                updatedRoot = root.ReplaceNode(local, local.ReplaceNode(node, replacement)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)).WithAdditionalAnnotations(Formatter.Annotation));

            var updated = context.Document.WithSyntaxRoot(updatedRoot);
            var updatedModel = await updated.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            // This also rejects lock bodies, constructors, expression trees, and incompatible returns.
            if (updatedModel is null || updatedModel.GetDiagnostics(cancellationToken: cancellationToken)
                .Any(d => d.Severity == DiagnosticSeverity.Error)) continue;
            context.RegisterCodeFix(CodeAction.Create(Title, _ => Task.FromResult(updated), Title), diagnostic);
        }
    }
}
