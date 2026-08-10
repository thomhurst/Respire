using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Respire.Analyzers;

/// <summary>
/// RESP002: a <c>RespirePending{T}</c> read before its batch was sent (or its transaction
/// committed) throws at runtime — the value simply is not there yet.
/// </summary>
/// <remarks>
/// Pragmatic and same-method only: the pending must come from a local batch/transaction in this
/// scope, and neither the pending nor the batch may leave it. The flush must be awaited on every
/// control-flow path to the read.
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

        if (ScopeWalker.IsInsideNameOf(context.SemanticModel, member, context.CancellationToken))
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
            || ScopeWalker.IsInsideNameOf(context.SemanticModel, binding, context.CancellationToken)
            || context.SemanticModel.GetSymbolInfo(binding, context.CancellationToken).Symbol is not IPropertySymbol property
            || !SymbolEqualityComparer.Default.Equals(property.ContainingType.OriginalDefinition, known.Pending)
            || binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is not { } conditional)
        {
            return;
        }

        AnalyzeRead(context, read: conditional, pending: conditional.Expression, known);
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
            || ResolveRootLocal(context, batchExpression) is not { } batch
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

        if (Escapes(context, scope, batch, allowReassignment: true)
            || HasFlushBefore(context, scope, batch, origin, read))
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
                    || local.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken)
                        is not VariableDeclaratorSyntax declarator)
                {
                    return null;
                }

                if (declarator.Initializer is not null)
                {
                    return !Escapes(context, scope, local)
                        ? ScopeWalker.Unwrap(declarator.Initializer.Value) as InvocationExpressionSyntax
                        : null;
                }

                var assignments = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => assignment.SpanStart < identifier.SpanStart
                                         && context.SemanticModel.GetSymbolInfo(
                                                 ScopeWalker.Unwrap(assignment.Left), context.CancellationToken).Symbol
                                             is ILocalSymbol assigned
                                         && SymbolEqualityComparer.Default.Equals(assigned, local))
                    .ToArray();
                if (assignments.Length != 1)
                {
                    return null;
                }

                var assignment = assignments[0];
                if (Escapes(context, scope, local, assignment))
                {
                    return null;
                }

                return ScopeWalker.Unwrap(assignment.Right) as InvocationExpressionSyntax;

            default:
                return null;
        }
    }

    /// <summary>True when the local is declared somewhere this scope cannot see all of its uses.</summary>
    private static bool IsDeclaredOutside(SyntaxNode scope, ILocalSymbol local)
        => local.DeclaringSyntaxReferences.Length != 1
           || !scope.Span.Contains(local.DeclaringSyntaxReferences[0].Span);

    private static ILocalSymbol? ResolveRootLocal(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        expression = ScopeWalker.Unwrap(expression);
        while (expression is MemberAccessExpressionSyntax member)
        {
            expression = ScopeWalker.Unwrap(member.Expression);
        }

        return context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol as ILocalSymbol;
    }

    /// <summary>
    /// True when the local is used in any way other than reading a member off it (or awaiting it) —
    /// passed on, returned, assigned or captured — which puts the flush out of this rule's reach.
    /// </summary>
    private static bool Escapes(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol local,
        AssignmentExpressionSyntax? allowedAssignment = null,
        bool allowReassignment = false)
    {
        foreach (var reference in ScopeWalker.FindReferences(scope, local, context.SemanticModel, context.CancellationToken))
        {
            if (ScopeWalker.IsInsideNameOf(context.SemanticModel, reference, context.CancellationToken))
            {
                continue;
            }

            if (ScopeWalker.IsNestedInLambda(reference, scope))
            {
                return true;
            }

            switch (reference.Parent)
            {
                case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, reference):
                    if (member.Parent is not InvocationExpressionSyntax
                        && context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol is IMethodSymbol)
                    {
                        return true;
                    }

                    if (member.Parent is InvocationExpressionSyntax invocation
                        && context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                            is IMethodSymbol { ReducedFrom: not null })
                    {
                        return true;
                    }

                    break;

                case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, reference):
                    break;

                case AwaitExpressionSyntax awaitExpression when ScopeWalker.IsSame(awaitExpression.Expression, reference):
                    break;

                case AssignmentExpressionSyntax assignment
                    when ScopeWalker.IsSame(assignment.Left, reference)
                         && (allowReassignment
                             || allowedAssignment is not null && ScopeWalker.IsSame(assignment, allowedAssignment)):
                    break;

                default:
                    return true;
            }
        }

        return false;
    }

    private static bool HasFlushBefore(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin,
        ExpressionSyntax read)
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

            if (!IsReassignedBetween(context, scope, batch, origin, invocation)
                && IsAwaitedBefore(context, scope, invocation, read))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReassignedBetween(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin,
        InvocationExpressionSyntax flush)
        => ScopeWalker.FindReferences(scope, batch, context.SemanticModel, context.CancellationToken)
            .Any(reference => reference.SpanStart > origin.SpanStart
                              && reference.SpanStart < flush.SpanStart
                              && reference.Parent is AssignmentExpressionSyntax assignment
                              && ScopeWalker.IsSame(assignment.Left, reference));

    private static bool IsAwaitedBefore(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        InvocationExpressionSyntax invocation,
        SyntaxNode read)
    {
        if (GetAwaitExpression(invocation) is { } directAwait
            && ScopeWalker.Dominates(context.SemanticModel, scope, directAwait, read, context.CancellationToken))
        {
            return true;
        }

        if (GetOutermostAwaitableExpression(invocation).Parent
                is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
            || context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken) is not ILocalSymbol flush)
        {
            return false;
        }

        foreach (var reference in ScopeWalker.FindReferences(
                     scope, flush, context.SemanticModel, context.CancellationToken))
        {
            if (GetAwaitExpression(reference) is { } awaitExpression
                && !IsReassignedBeforeAwait(context, scope, flush, invocation, awaitExpression)
                && ScopeWalker.Dominates(context.SemanticModel, scope, awaitExpression, read, context.CancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReassignedBeforeAwait(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol flush,
        InvocationExpressionSyntax initialization,
        AwaitExpressionSyntax awaitExpression)
        => ScopeWalker.FindReferences(scope, flush, context.SemanticModel, context.CancellationToken)
            .Where(reference => reference.SpanStart > initialization.SpanStart
                                && reference.SpanStart < awaitExpression.SpanStart)
            .Any(reference => reference.Parent is AssignmentExpressionSyntax assignment
                              && assignment.Left.Span.Contains(reference.Span));

    private static AwaitExpressionSyntax? GetAwaitExpression(ExpressionSyntax expression)
    {
        expression = GetOutermostAwaitableExpression(expression);
        return expression.Parent is AwaitExpressionSyntax awaitExpression
               && ScopeWalker.IsSame(awaitExpression.Expression, expression)
            ? awaitExpression
            : null;
    }

    private static ExpressionSyntax GetOutermostAwaitableExpression(ExpressionSyntax expression)
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
                Name.Identifier.ValueText: "ConfigureAwait" or "AsTask",
                Parent: InvocationExpressionSyntax configureAwait,
                } member
                && ScopeWalker.IsSame(member.Expression, expression))
            {
                expression = configureAwait;
                continue;
            }

            return expression;
        }
    }

    private sealed class KnownTypes(INamedTypeSymbol pending, INamedTypeSymbol? batch, INamedTypeSymbol? transaction)
    {
        public INamedTypeSymbol Pending { get; } = pending;

        public INamedTypeSymbol? Batch { get; } = batch;

        public INamedTypeSymbol? Transaction { get; } = transaction;
    }
}
