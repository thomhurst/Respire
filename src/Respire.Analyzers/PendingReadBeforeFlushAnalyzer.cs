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

            if (Escapes(
                    context,
                    scope,
                    batch,
                    allowReassignment: true,
                    allowNamedFlushExtension: true,
                    before: read)
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
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ExpressionSyntax pending,
        SyntaxNode read,
        ImmutableHashSet<ISymbol>? resolving = null)
    {
        switch (ScopeWalker.Unwrap(pending))
        {
            case InvocationExpressionSyntax inlineCall:
                yield return inlineCall;
                yield break;

            case ConditionalExpressionSyntax conditional:
                foreach (var origin in ResolveOriginatingCalls(
                             context, scope, conditional.WhenTrue, read, resolving))
                {
                    yield return origin;
                }

                foreach (var origin in ResolveOriginatingCalls(
                             context, scope, conditional.WhenFalse, read, resolving))
                {
                    yield return origin;
                }

                yield break;

            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var origin in ResolveOriginatingCalls(context, scope, coalesce.Left, read, resolving))
                {
                    yield return origin;
                }

                foreach (var origin in ResolveOriginatingCalls(context, scope, coalesce.Right, read, resolving))
                {
                    yield return origin;
                }

                yield break;

            case SwitchExpressionSyntax switchExpression:
                foreach (var arm in switchExpression.Arms)
                {
                    foreach (var origin in ResolveOriginatingCalls(
                                 context, scope, arm.Expression, read, resolving))
                    {
                        yield return origin;
                    }
                }

                yield break;

            case IdentifierNameSyntax identifier:
                if (context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is not ILocalSymbol local
                    || IsDeclaredOutside(scope, local)
                    || local.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken)
                        is not VariableDeclaratorSyntax declarator
                    || resolving?.Contains(local) == true)
                {
                    yield break;
                }

                resolving = (resolving ?? ImmutableHashSet.Create<ISymbol>(SymbolEqualityComparer.Default)).Add(local);

                if (Escapes(
                        context,
                        scope,
                        local,
                        allowReassignment: true,
                        allowLocalAlias: true,
                        before: read))
                {
                    yield break;
                }

                var definitions = new List<(SyntaxNode Write, ExpressionSyntax Value)>();
                if (declarator.Initializer is not null)
                {
                    definitions.Add((declarator, declarator.Initializer.Value));
                }

                definitions.AddRange(scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                          || assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression))
                                         && context.SemanticModel.GetSymbolInfo(
                                                 ScopeWalker.Unwrap(assignment.Left), context.CancellationToken).Symbol
                                             is ILocalSymbol assigned
                                         && SymbolEqualityComparer.Default.Equals(assigned, local))
                    .Select(static assignment => ((SyntaxNode)assignment, assignment.Right)));

                var writes = definitions.Select(static definition => definition.Write).ToArray();
                foreach (var definition in definitions)
                {
                    if (definition.Write is AssignmentExpressionSyntax coalesceAssignment
                        && coalesceAssignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)
                        && !CanCoalesceAssignmentExecute(context, scope, definition, definitions))
                    {
                        continue;
                    }

                    var otherWrites = writes.Where(write =>
                        !ScopeWalker.IsSame(write, definition.Write)
                        && !write.IsKind(SyntaxKind.CoalesceAssignmentExpression));
                    if (!ScopeWalker.CanReachWithoutCrossing(
                            context.SemanticModel,
                            scope,
                            definition.Write,
                            read,
                            otherWrites,
                            context.CancellationToken))
                    {
                        continue;
                    }

                    foreach (var origin in ResolveOriginatingCalls(
                                 context, scope, definition.Value, read, resolving))
                    {
                        yield return origin;
                    }
                }

                yield break;

            default:
                yield break;
        }
    }

    private static bool CanCoalesceAssignmentExecute(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        (SyntaxNode Write, ExpressionSyntax Value) coalesce,
        IReadOnlyList<(SyntaxNode Write, ExpressionSyntax Value)> definitions)
    {
        var reachingDefinitions = definitions.Where(candidate =>
            !ScopeWalker.IsSame(candidate.Write, coalesce.Write)
            && ScopeWalker.CanReachWithoutCrossing(
                context.SemanticModel,
                scope,
                candidate.Write,
                coalesce.Write,
                definitions.Where(barrier =>
                        !ScopeWalker.IsSame(barrier.Write, candidate.Write)
                        && !ScopeWalker.IsSame(barrier.Write, coalesce.Write))
                    .Select(static barrier => barrier.Write),
                context.CancellationToken)).ToArray();

        return reachingDefinitions.Length == 0
               || reachingDefinitions.Any(definition => !IsDefinitelyNonNullPending(context, definition.Value));
    }

    private static bool IsDefinitelyNonNullPending(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        => ScopeWalker.Unwrap(expression) is InvocationExpressionSyntax
           && context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type
               is INamedTypeSymbol { OriginalDefinition: { } definition }
           && definition.MetadataName == "RespirePending`1"
           && definition.ContainingNamespace.ToDisplayString() == "Respire";

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
        bool allowNamedFlushExtension = false,
        bool allowLocalAlias = false,
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

            var use = ScopeWalker.GetOutermostTransparentExpression(reference);

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
                        && (!allowNamedFlushExtension
                            || member.Name.Identifier.ValueText is not (SendAsync or CommitAsync))
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

                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
                    when allowLocalAlias
                         && context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken)
                             is ILocalSymbol:
                    break;

                case AssignmentExpressionSyntax assignment
                    when allowLocalAlias
                         && ScopeWalker.IsSame(assignment.Right, use)
                         && context.SemanticModel.GetSymbolInfo(
                             ScopeWalker.Unwrap(assignment.Left), context.CancellationToken).Symbol
                             is ILocalSymbol:
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
        if (HasBranchCorrelatedFlush(context, scope, batch, origin, read))
        {
            return true;
        }

        var completions = new List<SyntaxNode>();
        foreach (var invocation in scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!IsFlushInvocation(context, invocation, batch))
            {
                continue;
            }

            if (!IsReassignedBetween(context, scope, batch, origin, invocation))
            {
                completions.AddRange(GetCompletionExpressions(context, scope, invocation, batch, origin));
            }
        }

        return completions.Count > 0
               && ScopeWalker.CanReach(
                   context.SemanticModel, scope, origin, read, context.CancellationToken)
               && !ScopeWalker.CanReachWithoutCrossing(
                   context.SemanticModel, scope, origin, read, completions, context.CancellationToken);
    }

    private static bool HasBranchCorrelatedFlush(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin,
        ExpressionSyntax read)
    {
        var conditional = origin.Ancestors().OfType<ConditionalExpressionSyntax>().FirstOrDefault();
        if (conditional is not null)
        {
            return HasConditionalBranchFlush(context, scope, batch, origin, read, conditional);
        }

        var switchExpression = origin.Ancestors().OfType<SwitchExpressionSyntax>().FirstOrDefault();
        return switchExpression is not null
               && HasSwitchBranchFlush(context, scope, batch, origin, read, switchExpression);
    }

    private static bool HasConditionalBranchFlush(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin,
        ExpressionSyntax read,
        ConditionalExpressionSyntax conditional)
    {
        var selectedWhenTrue = conditional.WhenTrue.Span.Contains(origin.Span);
        foreach (var ifStatement in scope.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.SpanStart <= conditional.SpanStart
                || ifStatement.SpanStart >= read.SpanStart
                || !ScopeWalker.Dominates(
                    context.SemanticModel, scope, ifStatement, read, context.CancellationToken)
                || !SyntaxFactory.AreEquivalent(
                    ScopeWalker.Unwrap(conditional.Condition),
                    ScopeWalker.Unwrap(ifStatement.Condition))
                || ScopeWalker.HasWriteBetween(
                    context.SemanticModel,
                    scope,
                    conditional.Condition,
                    conditional,
                    ifStatement,
                    context.CancellationToken))
            {
                continue;
            }

            var branch = selectedWhenTrue ? ifStatement.Statement : ifStatement.Else?.Statement;
            if (branch is not null
                && HasUnconditionalFlush(context, scope, branch, batch, origin))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSwitchBranchFlush(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin,
        ExpressionSyntax read,
        SwitchExpressionSyntax switchExpression)
    {
        var arm = origin.Ancestors().OfType<SwitchExpressionArmSyntax>().First();
        foreach (var switchStatement in scope.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            if (switchStatement.SpanStart <= switchExpression.SpanStart
                || switchStatement.SpanStart >= read.SpanStart
                || !ScopeWalker.Dominates(
                    context.SemanticModel, scope, switchStatement, read, context.CancellationToken)
                || !SyntaxFactory.AreEquivalent(
                    ScopeWalker.Unwrap(switchExpression.GoverningExpression),
                    ScopeWalker.Unwrap(switchStatement.Expression))
                || ScopeWalker.HasWriteBetween(
                    context.SemanticModel,
                    scope,
                    switchExpression.GoverningExpression,
                    switchExpression,
                    switchStatement,
                    context.CancellationToken))
            {
                continue;
            }

            var section = switchStatement.Sections.FirstOrDefault(candidate =>
                candidate.Labels.Any(label => MatchesSwitchArm(arm, label)));
            if (section is not null
                && HasAlignedSwitchPrefix(switchExpression, switchStatement, arm, section)
                && HasUnconditionalFlush(context, scope, section, batch, origin))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAlignedSwitchPrefix(
        SwitchExpressionSyntax switchExpression,
        SwitchStatementSyntax switchStatement,
        SwitchExpressionArmSyntax arm,
        SwitchSectionSyntax section)
    {
        var armIndex = switchExpression.Arms.IndexOf(arm);
        var sectionIndex = switchStatement.Sections.IndexOf(section);
        if (armIndex < 0 || sectionIndex != armIndex)
        {
            return false;
        }

        for (var index = 0; index < armIndex; index++)
        {
            if (!switchStatement.Sections[index].Labels.Any(label =>
                    MatchesSwitchArm(switchExpression.Arms[index], label)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesSwitchArm(SwitchExpressionArmSyntax arm, SwitchLabelSyntax label)
        => (arm.Pattern, label) switch
        {
            (DiscardPatternSyntax, DefaultSwitchLabelSyntax) => arm.WhenClause is null,
            (ConstantPatternSyntax constant, CaseSwitchLabelSyntax @case) =>
                arm.WhenClause is null && SyntaxFactory.AreEquivalent(constant.Expression, @case.Value),
            (PatternSyntax pattern, CasePatternSwitchLabelSyntax @case) =>
                SyntaxFactory.AreEquivalent(pattern, @case.Pattern)
                && SyntaxFactory.AreEquivalent(arm.WhenClause?.Condition, @case.WhenClause?.Condition),
            _ => false,
        };

    private static bool HasUnconditionalFlush(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        SyntaxNode branch,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin)
    {
        foreach (var flush in branch.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (IsFlushInvocation(context, flush, batch)
                && GetCompletionExpression(context, flush) is { } completion
                && IsTopLevelBranchStatement(completion, branch)
                && !IsReassignedBetween(context, scope, batch, origin, flush))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTopLevelBranchStatement(SyntaxNode completion, SyntaxNode branch)
    {
        var statement = completion.FirstAncestorOrSelf<StatementSyntax>();
        return branch switch
        {
            BlockSyntax block => ReferenceEquals(statement?.Parent, block),
            SwitchSectionSyntax section => ReferenceEquals(statement?.Parent, section),
            StatementSyntax branchStatement => statement is not null
                                               && ScopeWalker.IsSame(statement, branchStatement),
            _ => false,
        };
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
            .Any(reference => reference.Parent is AssignmentExpressionSyntax assignment
                              && ScopeWalker.IsSame(assignment.Left, reference)
                              && ScopeWalker.CanReach(
                                  context.SemanticModel,
                                  scope,
                                  origin,
                                  assignment,
                                  context.CancellationToken)
                              && ScopeWalker.CanReach(
                                  context.SemanticModel,
                                  scope,
                                  assignment,
                                  flush,
                                  context.CancellationToken));

    private static IEnumerable<ExpressionSyntax> GetCompletionExpressions(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        InvocationExpressionSyntax invocation,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin)
    {
        if (GetCompletionExpression(context, invocation) is { } directCompletion)
        {
            yield return directCompletion;
            yield break;
        }

        if (ResolveStoredFlushLocal(context, GetOutermostCompletionExpression(context, invocation)) is not { } flush)
        {
            yield break;
        }

        foreach (var reference in ScopeWalker.FindReferences(
                     scope, flush, context.SemanticModel, context.CancellationToken))
        {
            if (GetCompletionExpression(context, reference) is { } completion
                && HasOnlyMatchingReachingDefinitions(context, scope, flush, batch, origin, completion))
            {
                yield return completion;
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

    private static bool HasOnlyMatchingReachingDefinitions(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol flush,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin,
        ExpressionSyntax completion)
    {
        var definitions = FindStoredValueDefinitions(context, scope, flush).ToArray();
        var reachingDefinitions = definitions.Where(definition =>
            ScopeWalker.CanReachWithoutCrossing(
                context.SemanticModel,
                scope,
                definition,
                completion,
                definitions.Where(candidate => !ScopeWalker.IsSame(candidate, definition)),
                context.CancellationToken)).ToArray();

        return reachingDefinitions.Length > 0
               && reachingDefinitions.All(definition =>
                   IsMatchingFlushDefinition(context, scope, definition, batch, origin))
               && !HasMutatingCallBefore(context, scope, flush, completion);
    }

    private static bool HasMutatingCallBefore(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ILocalSymbol local,
        ExpressionSyntax completion)
    {
        foreach (var reference in ScopeWalker.FindReferences(
                     scope, local, context.SemanticModel, context.CancellationToken))
        {
            var use = ScopeWalker.GetOutermostTransparentExpression(reference);
            if (use.SpanStart >= completion.SpanStart
                || use.Parent is not MemberAccessExpressionSyntax member
                || !ScopeWalker.IsSame(member.Expression, use)
                || member.Parent is not InvocationExpressionSyntax invocation
                || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                    is not IMethodSymbol method)
            {
                continue;
            }

            if (method.Name is "Add" or "AddRange" or "Clear" or "Insert" or "InsertRange"
                or "Remove" or "RemoveAll" or "RemoveAt" or "RemoveRange" or "Reverse" or "Sort")
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ExpressionSyntax> FindStoredValueDefinitions(
        SyntaxNodeAnalysisContext context, SyntaxNode scope, ILocalSymbol flush)
    {
        if (flush.DeclaringSyntaxReferences.Length == 1
            && flush.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken)
                is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
        {
            yield return initializer;
        }

        foreach (var reference in ScopeWalker.FindReferences(
                     scope, flush, context.SemanticModel, context.CancellationToken))
        {
            if (reference.FirstAncestorOrSelf<AssignmentExpressionSyntax>() is { } assignment
                && assignment.Left.Span.Contains(reference.Span))
            {
                yield return assignment.Right;
            }
        }
    }

    private static bool IsMatchingFlushDefinition(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        ExpressionSyntax definition,
        ILocalSymbol batch,
        InvocationExpressionSyntax origin)
    {
        var unwrappedDefinition = ScopeWalker.Unwrap(definition);
        foreach (var invocation in definition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (!ScopeWalker.IsSame(
                    ScopeWalker.Unwrap(GetOutermostCompletionExpression(context, invocation)),
                    unwrappedDefinition)
                || !IsFlushInvocation(context, invocation, batch))
            {
                continue;
            }

            return !IsReassignedBetween(context, scope, batch, origin, invocation);
        }

        return false;
    }

    private static bool IsFlushInvocation(
        SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, ILocalSymbol batch)
    {
        var expectedName = batch.Type.ToDisplayString() == TransactionTypeName ? CommitAsync : SendAsync;
        return ScopeWalker.Unwrap(invocation.Expression) is MemberAccessExpressionSyntax member
               && member.Name.Identifier.ValueText == expectedName
               && context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                   is IMethodSymbol method
               && method.Name == expectedName
               && SymbolEqualityComparer.Default.Equals(method.ContainingType, batch.Type)
               && context.SemanticModel.GetSymbolInfo(
                   ScopeWalker.Unwrap(member.Expression), context.CancellationToken).Symbol is ILocalSymbol target
               && SymbolEqualityComparer.Default.Equals(target, batch);
    }

    private static ExpressionSyntax? GetCompletionExpression(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        expression = GetOutermostCompletionExpression(context, expression);
        if (expression.Parent is AwaitExpressionSyntax awaitExpression
            && ScopeWalker.IsSame(awaitExpression.Expression, expression))
        {
            return awaitExpression;
        }

        return GetSynchronousGetResult(context, expression);
    }

    private static ExpressionSyntax GetOutermostCompletionExpression(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        while (true)
        {
            expression = GetOutermostAwaitableExpression(context, expression);

            if (expression.Parent is InitializerExpressionSyntax initializer
                && initializer.Expressions.Any(candidate => ScopeWalker.IsSame(candidate, expression))
                && initializer.Parent is ExpressionSyntax collection)
            {
                expression = collection;
                continue;
            }

            if (expression.Parent is ExpressionElementSyntax
                {
                    Parent: CollectionExpressionSyntax collectionExpression,
                })
            {
                expression = collectionExpression;
                continue;
            }

            if (expression.Parent is not ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax combinator }
                || context.SemanticModel.GetSymbolInfo(combinator, context.CancellationToken).Symbol
                    is not IMethodSymbol { Name: nameof(Task.WhenAll) } method
                || method.ContainingType.ToDisplayString() != typeof(Task).FullName)
            {
                return expression;
            }

            expression = combinator;
        }
    }

    private static InvocationExpressionSyntax? GetSynchronousGetResult(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression.Parent is not MemberAccessExpressionSyntax getAwaiterMember
            || !ScopeWalker.IsSame(getAwaiterMember.Expression, expression)
            || getAwaiterMember.Parent is not InvocationExpressionSyntax getAwaiterInvocation
            || getAwaiterInvocation.ArgumentList.Arguments.Count != 0
            || context.SemanticModel.GetSymbolInfo(getAwaiterInvocation, context.CancellationToken).Symbol
                is not IMethodSymbol { Name: GetAwaiter }
            || getAwaiterInvocation.Parent is not MemberAccessExpressionSyntax getResultMember
            || !ScopeWalker.IsSame(getResultMember.Expression, getAwaiterInvocation)
            || getResultMember.Parent is not InvocationExpressionSyntax getResultInvocation
            || getResultInvocation.ArgumentList.Arguments.Count != 0
            || context.SemanticModel.GetSymbolInfo(getResultInvocation, context.CancellationToken).Symbol
                is not IMethodSymbol { Name: GetResult })
        {
            return null;
        }

        return getResultInvocation;
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
