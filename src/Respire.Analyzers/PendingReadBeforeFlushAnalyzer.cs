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
    private const string GetAwaiter = "GetAwaiter";
    private const string GetResult = "GetResult";

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
            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeManualGetResult(nodeContext, known), SyntaxKind.InvocationExpression);
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

    private static void AnalyzeManualGetResult(SyntaxNodeAnalysisContext context, KnownTypes known)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (ScopeWalker.Unwrap(invocation.Expression) is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: GetResult,
            } getResult
            || ScopeWalker.Unwrap(getResult.Expression) is not InvocationExpressionSyntax getAwaiterInvocation
            || ScopeWalker.Unwrap(getAwaiterInvocation.Expression) is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: GetAwaiter,
            } getAwaiter
            || context.SemanticModel.GetSymbolInfo(getAwaiter, context.CancellationToken).Symbol
                is not IMethodSymbol getAwaiterMethod
            || !SymbolEqualityComparer.Default.Equals(
                getAwaiterMethod.ContainingType.OriginalDefinition, known.Pending))
        {
            return;
        }

        AnalyzeRead(context, read: invocation, pending: getAwaiter.Expression, known);
    }

    private static void AnalyzeRead(SyntaxNodeAnalysisContext context, ExpressionSyntax read, ExpressionSyntax pending, KnownTypes known)
    {
        var scope = ScopeWalker.GetEnclosingScope(read);
        if (scope is null)
        {
            return;
        }

        var origins = ResolveOriginatingCalls(context, scope, pending, read).ToArray();
        if (origins.Length == 0)
        {
            return;
        }

        foreach (var origin in origins)
        {
            if (ScopeWalker.GetReceiver(origin.Expression) is not { } batchExpression
                || ResolveRootLocal(context, batchExpression) is not { } batch
                || IsDeclaredOutside(scope, batch))
            {
                // A field, a parameter, or a batch built further out: it outlives this scope.
                continue;
            }

            var isTransaction = SymbolEqualityComparer.Default.Equals(batch.Type, known.Transaction);
            if (!isTransaction && !SymbolEqualityComparer.Default.Equals(batch.Type, known.Batch))
            {
                continue;
            }

            if (Escapes(context, scope, batch, allowReassignment: true, before: read)
                || HasFlushBefore(context, scope, batch, origin, read))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule, read.GetLocation(), batch.Name, isTransaction ? CommitAsync : SendAsync));
            return;
        }
    }

    /// <summary>
    /// The <c>batch.SomethingAsync(…)</c> call that produced the pending being read, whether it was
    /// read inline or through a local that never leaves this scope.
    /// </summary>
    private static IEnumerable<InvocationExpressionSyntax> ResolveOriginatingCalls(
        SyntaxNodeAnalysisContext context, SyntaxNode scope, ExpressionSyntax pending, SyntaxNode read)
    {
        switch (ScopeWalker.Unwrap(pending))
        {
            case InvocationExpressionSyntax inlineCall:
                yield return inlineCall;
                yield break;

            case IdentifierNameSyntax identifier:
                if (context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is not ILocalSymbol local
                    || IsDeclaredOutside(scope, local)
                    || local.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken)
                        is not VariableDeclaratorSyntax declarator)
                {
                    yield break;
                }

                if (Escapes(context, scope, local, allowReassignment: true, before: read))
                {
                    yield break;
                }

                var definitions = new List<(SyntaxNode Write, ExpressionSyntax Value)>();
                if (declarator.Initializer is not null)
                {
                    definitions.Add((declarator, declarator.Initializer.Value));
                }

                definitions.AddRange(scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                         && context.SemanticModel.GetSymbolInfo(
                                                 ScopeWalker.Unwrap(assignment.Left), context.CancellationToken).Symbol
                                             is ILocalSymbol assigned
                                         && SymbolEqualityComparer.Default.Equals(assigned, local))
                    .Select(static assignment => ((SyntaxNode)assignment, assignment.Right)));

                var writes = definitions.Select(static definition => definition.Write).ToArray();
                foreach (var definition in definitions)
                {
                    var otherWrites = writes.Where(write => !ScopeWalker.IsSame(write, definition.Write));
                    if (ScopeWalker.Unwrap(definition.Value) is InvocationExpressionSyntax invocation
                        && ScopeWalker.CanReachWithoutCrossing(
                            context.SemanticModel,
                            scope,
                            definition.Write,
                            read,
                            otherWrites,
                            context.CancellationToken))
                    {
                        yield return invocation;
                    }
                }

                yield break;

            default:
                yield break;
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
        bool allowReassignment = false,
        SyntaxNode? before = null)
    {
        foreach (var reference in ScopeWalker.FindReferences(scope, local, context.SemanticModel, context.CancellationToken))
        {
            if (before is not null && reference.SpanStart > before.SpanStart)
            {
                continue;
            }

            if (ScopeWalker.IsInsideNameOf(context.SemanticModel, reference, context.CancellationToken))
            {
                continue;
            }

            if (ScopeWalker.IsDiscarded(reference))
            {
                continue;
            }

            if (ScopeWalker.IsNestedInLambda(reference, scope))
            {
                if (DominatesRead(context, scope, reference, before))
                {
                    return true;
                }

                continue;
            }

            ExpressionSyntax use = reference;
            while (true)
            {
                if (use.Parent is ParenthesizedExpressionSyntax parenthesized
                    && ScopeWalker.IsSame(parenthesized.Expression, use))
                {
                    use = parenthesized;
                    continue;
                }

                if (use.Parent is PostfixUnaryExpressionSyntax suppression
                    && suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression)
                    && ScopeWalker.IsSame(suppression.Operand, use))
                {
                    use = suppression;
                    continue;
                }

                break;
            }

            switch (use.Parent)
            {
                case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, use):
                    if (member.Parent is not InvocationExpressionSyntax
                        && context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol is IMethodSymbol
                        && DominatesRead(context, scope, member, before))
                    {
                        return true;
                    }

                    if (member.Parent is InvocationExpressionSyntax invocation
                        && context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                            is IMethodSymbol { ReducedFrom: not null }
                        && DominatesRead(context, scope, invocation, before))
                    {
                        return true;
                    }

                    break;

                case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, use):
                    break;

                case AwaitExpressionSyntax awaitExpression when ScopeWalker.IsSame(awaitExpression.Expression, use):
                    break;

                case AssignmentExpressionSyntax assignment
                    when ScopeWalker.IsSame(assignment.Left, use)
                         && (allowReassignment
                             || allowedAssignment is not null && ScopeWalker.IsSame(assignment, allowedAssignment)):
                    break;

                default:
                    if (DominatesRead(context, scope, use, before))
                    {
                        return true;
                    }

                    break;
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
        var completions = new List<SyntaxNode>();
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

            if (!IsReassignedBetween(context, scope, batch, origin, invocation))
            {
                completions.AddRange(GetAwaitExpressions(context, scope, invocation));
            }
        }

        return ScopeWalker.CollectivelyDominates(
            context.SemanticModel, scope, completions, read, context.CancellationToken);
    }

    private static bool DominatesRead(
        SyntaxNodeAnalysisContext context, SyntaxNode scope, SyntaxNode escape, SyntaxNode? read)
        => read is null
           || ScopeWalker.Dominates(
               context.SemanticModel, scope, escape, read, context.CancellationToken);

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

    private static IEnumerable<AwaitExpressionSyntax> GetAwaitExpressions(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        InvocationExpressionSyntax invocation)
    {
        if (GetAwaitExpression(context, invocation) is { } directAwait)
        {
            yield return directAwait;
            yield break;
        }

        if (ResolveStoredFlushLocal(context, GetOutermostAwaitableExpression(context, invocation)) is not { } flush)
        {
            yield break;
        }

        foreach (var reference in ScopeWalker.FindReferences(
                     scope, flush, context.SemanticModel, context.CancellationToken))
        {
            if (GetAwaitExpression(context, reference) is { } awaitExpression
                && !IsReassignedBeforeAwait(context, scope, flush, invocation, awaitExpression))
            {
                yield return awaitExpression;
            }
        }
    }

    private static ILocalSymbol? ResolveStoredFlushLocal(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        => expression.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } =>
                context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken) as ILocalSymbol,
            AssignmentExpressionSyntax assignment
                when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                     && ScopeWalker.IsSame(assignment.Right, expression) =>
                context.SemanticModel.GetSymbolInfo(
                    ScopeWalker.Unwrap(assignment.Left), context.CancellationToken).Symbol as ILocalSymbol,
            _ => null,
        };

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

    private static AwaitExpressionSyntax? GetAwaitExpression(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        while (true)
        {
            expression = GetOutermostAwaitableExpression(context, expression);
            if (expression.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax combinator }
                && context.SemanticModel.GetSymbolInfo(combinator, context.CancellationToken).Symbol
                    is IMethodSymbol { Name: nameof(Task.WhenAll) } method
                && method.ContainingType.ToDisplayString() == typeof(Task).FullName)
            {
                expression = combinator;
                continue;
            }

            return expression.Parent is AwaitExpressionSyntax awaitExpression
                   && ScopeWalker.IsSame(awaitExpression.Expression, expression)
                ? awaitExpression
                : null;
        }
    }

    private static ExpressionSyntax GetOutermostAwaitableExpression(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
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
                    Parent: InvocationExpressionSyntax adapter,
                } member
                && ScopeWalker.IsSame(member.Expression, expression)
                && IsAwaitAdapter(context, adapter))
            {
                expression = adapter;
                continue;
            }

            return expression;
        }
    }

    private static bool IsAwaitAdapter(
        SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            is not IMethodSymbol method)
        {
            return false;
        }

        return method.Name switch
        {
            "ConfigureAwait" or "AsTask" => true,
            "WaitAsync" => method.ContainingType.Name == nameof(Task)
                           && method.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks",
            _ => false,
        };
    }

    private sealed class KnownTypes(INamedTypeSymbol pending, INamedTypeSymbol? batch, INamedTypeSymbol? transaction)
    {
        public INamedTypeSymbol Pending { get; } = pending;

        public INamedTypeSymbol? Batch { get; } = batch;

        public INamedTypeSymbol? Transaction { get; } = transaction;
    }
}
