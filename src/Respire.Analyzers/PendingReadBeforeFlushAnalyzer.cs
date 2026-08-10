using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>
/// RESP002: a <c>RespirePending{T}</c> read before its batch was sent (or its transaction
/// committed) throws at runtime — the value simply is not there yet.
/// </summary>
/// <remarks>
/// Pragmatic and same-method only: the pending must come from a local batch/transaction in this
/// scope, and neither the pending nor the batch may leave it. The flush is matched textually and
/// must be awaited before the read.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PendingReadBeforeFlushAnalyzer : DiagnosticAnalyzer
{
    internal const string PendingTypeName = "Respire.RespirePending`1";
    internal const string BatchTypeName = "Respire.RespireBatch";
    internal const string TransactionTypeName = "Respire.RespireTransaction";

    private const string SendAsync = "SendAsync";
    private const string CommitAsync = "CommitAsync";
    private const string ResultPropertyName = "Result";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.PendingReadBeforeFlush,
        title: "Pending result is read before the batch is sent",
        messageFormat: "This pending result is read before '{0}' is flushed; await '{0}.{1}()' first",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A RespirePending<T> only carries a value once its batch has been sent or its "
            + "transaction committed; reading it earlier throws InvalidOperationException. "
            + "Queue the commands, flush, then read the pendings.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var pendingType = compilationStart.Compilation.GetTypeByMetadataName(PendingTypeName);
            if (pendingType is null)
            {
                return;
            }

            var batchType = compilationStart.Compilation.GetTypeByMetadataName(BatchTypeName);
            var transactionType = compilationStart.Compilation.GetTypeByMetadataName(TransactionTypeName);
            if (batchType is null && transactionType is null)
            {
                return;
            }

            var known = new KnownTypes(pendingType, batchType, transactionType);

            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeResultAccess(nodeContext, known), SyntaxKind.SimpleMemberAccessExpression);
            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeResultBinding(nodeContext, known), SyntaxKind.MemberBindingExpression);
            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeAwait(nodeContext, known), SyntaxKind.AwaitExpression);
        });
    }

    private static void AnalyzeResultAccess(SyntaxNodeAnalysisContext context, KnownTypes known)
    {
        var member = (MemberAccessExpressionSyntax)context.Node;
        if (member.Name.Identifier.ValueText != ResultPropertyName)
        {
            return;
        }

        if (IsInsideNameOf(context, member))
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol is not IPropertySymbol property
            || !SymbolEqualityComparer.Default.Equals(property.ContainingType.OriginalDefinition, known.Pending))
        {
            return;
        }

        AnalyzeRead(context, read: member, pending: member.Expression, known);
    }

    private static void AnalyzeResultBinding(SyntaxNodeAnalysisContext context, KnownTypes known)
    {
        var binding = (MemberBindingExpressionSyntax)context.Node;
        if (binding.Name.Identifier.ValueText != ResultPropertyName
            || IsInsideNameOf(context, binding)
            || context.SemanticModel.GetSymbolInfo(binding, context.CancellationToken).Symbol is not IPropertySymbol property
            || !SymbolEqualityComparer.Default.Equals(property.ContainingType.OriginalDefinition, known.Pending)
            || binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is not { } conditional)
        {
            return;
        }

        AnalyzeRead(context, read: conditional, pending: conditional.Expression, known);
    }

    private static bool IsInsideNameOf(SyntaxNodeAnalysisContext context, SyntaxNode node)
    {
        for (var operation = context.SemanticModel.GetOperation(node, context.CancellationToken);
             operation is not null;
             operation = operation.Parent)
        {
            if (operation is INameOfOperation)
            {
                return true;
            }
        }

        return false;
    }

    private static void AnalyzeAwait(SyntaxNodeAnalysisContext context, KnownTypes known)
    {
        var awaitExpression = (AwaitExpressionSyntax)context.Node;
        var awaited = ScopeWalker.Unwrap(awaitExpression.Expression);

        var awaitedType = context.SemanticModel.GetTypeInfo(awaited, context.CancellationToken).Type;
        if (awaitedType is null || !SymbolEqualityComparer.Default.Equals(awaitedType.OriginalDefinition, known.Pending))
        {
            return;
        }

        AnalyzeRead(context, read: awaitExpression, pending: awaited, known);
    }

    private static void AnalyzeRead(SyntaxNodeAnalysisContext context, ExpressionSyntax read, ExpressionSyntax pending, KnownTypes known)
    {
        var scope = ScopeWalker.GetEnclosingScope(read);
        if (scope is null)
        {
            return;
        }

        var origin = ResolveOriginatingCall(context, scope, pending);
        if (origin is null)
        {
            return;
        }

        if (ScopeWalker.GetReceiver(origin.Expression) is not { } batchExpression
            || context.SemanticModel.GetSymbolInfo(batchExpression, context.CancellationToken).Symbol is not ILocalSymbol batch
            || IsDeclaredOutside(scope, batch))
        {
            // A field, a parameter, or a batch built further out: it outlives this scope, so stay silent.
            return;
        }

        var isTransaction = SymbolEqualityComparer.Default.Equals(batch.Type, known.Transaction);
        if (!isTransaction && !SymbolEqualityComparer.Default.Equals(batch.Type, known.Batch))
        {
            return;
        }

        if (Escapes(context, scope, batch) || HasFlushBefore(context, scope, batch, read))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, read.GetLocation(), batch.Name, isTransaction ? CommitAsync : SendAsync));
    }

    /// <summary>
    /// The <c>batch.SomethingAsync(…)</c> call that produced the pending being read, whether it was
    /// read inline or through a local that never leaves this scope.
    /// </summary>
    private static InvocationExpressionSyntax? ResolveOriginatingCall(
        SyntaxNodeAnalysisContext context, SyntaxNode scope, ExpressionSyntax pending)
    {
        switch (ScopeWalker.Unwrap(pending))
        {
            case InvocationExpressionSyntax inlineCall:
                return inlineCall;

            case IdentifierNameSyntax identifier:
                if (context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is not ILocalSymbol local
                    || IsDeclaredOutside(scope, local)
                    || Escapes(context, scope, local))
                {
                    return null;
                }

                return local.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken) is VariableDeclaratorSyntax declarator
                       && declarator.Initializer is not null
                    ? ScopeWalker.Unwrap(declarator.Initializer.Value) as InvocationExpressionSyntax
                    : null;

            default:
                return null;
        }
    }

    /// <summary>True when the local is declared somewhere this scope cannot see all of its uses.</summary>
    private static bool IsDeclaredOutside(SyntaxNode scope, ILocalSymbol local)
        => local.DeclaringSyntaxReferences.Length != 1
           || !scope.Span.Contains(local.DeclaringSyntaxReferences[0].Span);

    /// <summary>
    /// True when the local is used in any way other than reading a member off it (or awaiting it) —
    /// passed on, returned, assigned or captured — which puts the flush out of this rule's reach.
    /// </summary>
    private static bool Escapes(SyntaxNodeAnalysisContext context, SyntaxNode scope, ILocalSymbol local)
    {
        foreach (var reference in ScopeWalker.FindReferences(scope, local, context.SemanticModel, context.CancellationToken))
        {
            if (ScopeWalker.IsNestedInLambda(reference, scope))
            {
                return true;
            }

            switch (reference.Parent)
            {
                case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, reference):
                    break;

                case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, reference):
                    break;

                case AwaitExpressionSyntax awaitExpression when ScopeWalker.IsSame(awaitExpression.Expression, reference):
                    break;

                default:
                    return true;
            }
        }

        return false;
    }

    private static bool HasFlushBefore(SyntaxNodeAnalysisContext context, SyntaxNode scope, ILocalSymbol batch, ExpressionSyntax read)
    {
        foreach (var invocation in scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (ScopeWalker.Unwrap(invocation.Expression) is not MemberAccessExpressionSyntax member)
            {
                continue;
            }

            var name = member.Name.Identifier.ValueText;
            if (name != SendAsync && name != CommitAsync)
            {
                continue;
            }

            if (context.SemanticModel.GetSymbolInfo(member.Expression, context.CancellationToken).Symbol is not ILocalSymbol target
                || !SymbolEqualityComparer.Default.Equals(target, batch))
            {
                continue;
            }

            if (IsAwaitedBefore(context, scope, invocation, read))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAwaitedBefore(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        InvocationExpressionSyntax invocation,
        SyntaxNode read)
    {
        if (GetAwaitExpression(invocation) is { } directAwait && directAwait.SpanStart < read.SpanStart)
        {
            return true;
        }

        if (invocation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
            || context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken) is not ILocalSymbol flush)
        {
            return false;
        }

        foreach (var reference in ScopeWalker.FindReferences(
                     scope, flush, context.SemanticModel, context.CancellationToken))
        {
            if (GetAwaitExpression(reference) is { } awaitExpression && awaitExpression.SpanStart < read.SpanStart)
            {
                return true;
            }
        }

        return false;
    }

    private static AwaitExpressionSyntax? GetAwaitExpression(ExpressionSyntax expression)
    {
        while (true)
        {
            if (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized;
                continue;
            }

            if (expression.Parent is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "ConfigureAwait",
                    Parent: InvocationExpressionSyntax configureAwait,
                } member
                && ScopeWalker.IsSame(member.Expression, expression))
            {
                expression = configureAwait;
                continue;
            }

            return expression.Parent is AwaitExpressionSyntax awaitExpression
                   && ScopeWalker.IsSame(awaitExpression.Expression, expression)
                ? awaitExpression
                : null;
        }
    }

    private sealed class KnownTypes(INamedTypeSymbol pending, INamedTypeSymbol? batch, INamedTypeSymbol? transaction)
    {
        public INamedTypeSymbol Pending { get; } = pending;

        public INamedTypeSymbol? Batch { get; } = batch;

        public INamedTypeSymbol? Transaction { get; } = transaction;
    }
}
