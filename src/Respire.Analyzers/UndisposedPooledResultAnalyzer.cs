using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>
/// RESP001: a <c>RespireResult</c> or <c>RespireLease</c> awaited into a local and then dropped
/// without disposal leaks its pooled buffer.
/// </summary>
/// <remarks>
/// The analysis is deliberately conservative: it only reports a local whose every use is a
/// non-owning read (<c>result.AsString()</c>, <c>result[0]</c>, …). The moment the value is passed
/// on, returned, assigned, or captured, ownership belongs to code this rule cannot see and it stays
/// silent. Nested results from the indexer are views over the root, are never obtained from an
/// <c>await</c>, and so are never candidates.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedPooledResultAnalyzer : DiagnosticAnalyzer
{
    internal const string ResultTypeName = "Respire.RespireResult";
    internal const string LeaseTypeName = "Respire.RespireLease";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.UndisposedPooledResult,
        title: "Pooled Respire result is never disposed",
        messageFormat: "'{0}' holds a pooled {1} that is never disposed; declare it with 'using' so the buffer returns to the pool",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "RespireResult and RespireLease are leases over pooled memory. A local that is awaited "
            + "into existence and then never disposed keeps that buffer out of the pool for good. "
            + "Declare it with a 'using' declaration (or dispose it explicitly).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(static compilationStart =>
        {
            var resultType = compilationStart.Compilation.GetTypeByMetadataName(ResultTypeName);
            var leaseType = compilationStart.Compilation.GetTypeByMetadataName(LeaseTypeName);
            if (resultType is null && leaseType is null)
            {
                return;
            }

            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeDeclaration(nodeContext, resultType, leaseType),
                SyntaxKind.LocalDeclarationStatement);
            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeAssignment(nodeContext, resultType, leaseType),
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxKind.CoalesceAssignmentExpression);
        });
    }

    private static void AnalyzeDeclaration(
        SyntaxNodeAnalysisContext context, INamedTypeSymbol? resultType, INamedTypeSymbol? leaseType)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;

        // `using var result = …` / `using (…)` already own the disposal.
        if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
        {
            return;
        }

        var scope = ScopeWalker.GetEnclosingScope(declaration);
        if (scope is null)
        {
            return;
        }

        foreach (var variable in declaration.Declaration.Variables)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (variable.Initializer is null
                || !ContainsRespireAcquisition(context, variable.Initializer.Value))
            {
                // Only an awaited Respire API call hands out a fresh lease; indexer views, copies,
                // and values forwarded through Task/ValueTask helpers keep their existing owner.
                continue;
            }

            if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not ILocalSymbol local)
            {
                continue;
            }

            var pooledType = Match(local.Type, resultType) ?? Match(local.Type, leaseType);
            if (pooledType is null)
            {
                continue;
            }

            if (IsDisposedOrEscapes(context, scope, declaration, acquisitionAssignment: null, local))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), local.Name, pooledType.Name));
        }
    }

    private static void AnalyzeAssignment(
        SyntaxNodeAnalysisContext context, INamedTypeSymbol? resultType, INamedTypeSymbol? leaseType)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (ScopeWalker.Unwrap(assignment.Left) is not IdentifierNameSyntax identifier
            || context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is not ILocalSymbol local
            || !ContainsRespireAcquisition(context, assignment.Right)
            || ScopeWalker.GetEnclosingScope(assignment) is not { } scope
            || local.DeclaringSyntaxReferences.Length != 1
            || !scope.Span.Contains(local.DeclaringSyntaxReferences[0].Span))
        {
            return;
        }

        var pooledType = Match(local.Type, resultType) ?? Match(local.Type, leaseType);
        if (pooledType is null || IsDisposedOrEscapes(context, scope, assignment.Right, assignment, local))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), local.Name, pooledType.Name));
    }

    private static bool ContainsRespireAcquisition(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        switch (ScopeWalker.Unwrap(expression))
        {
            case AwaitExpressionSyntax awaitExpression:
                return IsRespireAcquisition(context, awaitExpression.Expression);

            case ConditionalExpressionSyntax conditional:
                return ContainsRespireAcquisition(context, conditional.WhenTrue)
                       || ContainsRespireAcquisition(context, conditional.WhenFalse);

            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                return ContainsRespireAcquisition(context, coalesce.Left)
                       || ContainsRespireAcquisition(context, coalesce.Right);

            case SwitchExpressionSyntax switchExpression:
                return switchExpression.Arms.Any(arm =>
                    ContainsRespireAcquisition(context, arm.Expression));

            default:
                return false;
        }
    }

    private static INamedTypeSymbol? Match(ITypeSymbol candidate, INamedTypeSymbol? pooledType)
    {
        if (candidate is INamedTypeSymbol nullable
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && nullable.TypeArguments.Length == 1)
        {
            candidate = nullable.TypeArguments[0];
        }

        return pooledType is not null && SymbolEqualityComparer.Default.Equals(candidate, pooledType)
            ? pooledType
            : null;
    }

    private static bool IsRespireAcquisition(SyntaxNodeAnalysisContext context, ExpressionSyntax awaitedExpression)
    {
        var expression = ScopeWalker.Unwrap(awaitedExpression);

        // await client.ExecuteAsync(...).AsTask().WaitAsync(token).ConfigureAwait(false)
        while (expression is InvocationExpressionSyntax adapter
               && context.SemanticModel.GetSymbolInfo(adapter, context.CancellationToken).Symbol
                   is IMethodSymbol adapterMethod
               && IsAwaitAdapter(adapterMethod)
               && ScopeWalker.GetReceiver(adapter.Expression) is { } adaptedReceiver)
        {
            expression = ScopeWalker.Unwrap(adaptedReceiver);
        }

        if (expression is not InvocationExpressionSyntax invocation
            || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return false;
        }

        var containingType = method.ContainingType?.ToDisplayString();
        return method.Name switch
        {
            "ExecuteAsync" => containingType is "Respire.RespireClient"
                or "Respire.IRespireClient"
                or "Respire.ScriptCommands"
                or "Respire.IScriptCommands",
            "GetLeaseAsync" => containingType is "Respire.RespireClient"
                or "Respire.IRespireClient"
                or "Respire.StringCommands"
                or "Respire.IStringCommands",
            _ => false,
        };
    }

    private static bool IsAwaitAdapter(IMethodSymbol method)
        => method.Name switch
        {
            nameof(Task.ConfigureAwait) or "AsTask" => true,
            "WaitAsync" => method.ContainingType.Name == nameof(Task)
                           && method.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks",
            _ => false,
        };

    /// <summary>
    /// True when disposal or ownership transfer covers every path from acquisition to scope exit.
    /// </summary>
    private static bool IsDisposedOrEscapes(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        SyntaxNode acquisition,
        AssignmentExpressionSyntax? acquisitionAssignment,
        ILocalSymbol local)
    {
        var references = ScopeWalker.FindReferences(
            scope, local, context.SemanticModel, context.CancellationToken).ToArray();
        var reassignments = references
            .Where(reference => reference.Parent is AssignmentExpressionSyntax assignment
                                && ScopeWalker.IsSame(assignment.Left, reference)
                                && !assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression))
            .Select(static reference => (AssignmentExpressionSyntax)reference.Parent!)
            .ToArray();
        var releases = new List<SyntaxNode>();
        if (acquisitionAssignment is not null
            && IsImmediatelyReleasedOrTransferred(context, acquisitionAssignment))
        {
            return true;
        }

        foreach (var reference in references)
        {
            if (reference.SpanStart < acquisition.SpanStart
                || acquisitionAssignment is not null
                && reference.Parent is AssignmentExpressionSyntax acquisitionWrite
                && ScopeWalker.IsSame(acquisitionWrite, acquisitionAssignment))
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

            if (reassignments.Any(reassignment =>
                    ScopeWalker.CanReachWithoutCrossing(
                        context.SemanticModel,
                        scope,
                        acquisition,
                        reassignment,
                        [reference],
                        context.CancellationToken)
                    && ScopeWalker.CanReach(
                        context.SemanticModel, scope, reassignment, reference, context.CancellationToken)))
            {
                // Later uses refer to the replacement, not the acquired pooled owner.
                continue;
            }

            if (ScopeWalker.IsNestedInLambda(reference, scope))
            {
                releases.Add(reference);
                continue;
            }

            var use = ScopeWalker.GetOutermostTransparentExpression(reference);
            switch (use.Parent)
            {
                // result.AsString(), result.Type, result.Dispose()
                case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, use):
                    if (member.Name.Identifier.ValueText == nameof(IDisposable.Dispose)
                        && member.Parent is not InvocationExpressionSyntax)
                    {
                        releases.Add(member);
                    }

                    if (member.Parent is InvocationExpressionSyntax extensionInvocation
                        && context.SemanticModel.GetSymbolInfo(extensionInvocation, context.CancellationToken).Symbol
                            is IMethodSymbol { ReducedFrom: not null })
                    {
                        releases.Add(extensionInvocation);
                    }

                    if (member.Name.Identifier.ValueText == nameof(IDisposable.Dispose)
                        && member.Parent is InvocationExpressionSyntax invocation
                        && ScopeWalker.IsSame(invocation.Expression, member))
                    {
                        releases.Add(invocation);
                    }

                    break;

                // result[0] — a non-owning view of an element.
                case ElementAccessExpressionSyntax element when ScopeWalker.IsSame(element.Expression, use):
                    break;

                // result?.Dispose()
                case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, use):
                    var conditionalDispose = conditional.WhenNotNull.DescendantNodesAndSelf()
                        .OfType<MemberBindingExpressionSyntax>()
                        .Where(binding => binding.Name.Identifier.ValueText == nameof(IDisposable.Dispose)
                                          && binding.Parent is InvocationExpressionSyntax invocation
                                          && ScopeWalker.IsSame(invocation.Expression, binding))
                        .Select(static binding => (InvocationExpressionSyntax)binding.Parent!)
                        .FirstOrDefault();
                    if (conditionalDispose is not null)
                    {
                        // A Respire acquisition always supplies a value. Treat the whole conditional
                        // access as the release barrier so nullable flow does not invent a null branch.
                        releases.Add(conditional);
                    }

                    break;

                // using (result) { … }
                case UsingStatementSyntax usingStatement when usingStatement.Expression is not null
                                                              && ScopeWalker.IsSame(usingStatement.Expression, use):
                    releases.Add(usingStatement);
                    break;

                // result = … drops the acquired owner; it does not transfer ownership elsewhere.
                case AssignmentExpressionSyntax assignment when ScopeWalker.IsSame(assignment.Left, use):
                    break;

                // Anything else — an argument, a return, or an assignment source — hands ownership away.
                default:
                    releases.Add(use);
                    break;
            }
        }

        return ScopeWalker.CollectivelyPostDominates(
            context.SemanticModel, scope, acquisition, releases, context.CancellationToken);
    }

    private static bool IsImmediatelyReleasedOrTransferred(
        SyntaxNodeAnalysisContext context, AssignmentExpressionSyntax assignment)
    {
        var use = ScopeWalker.GetOutermostTransparentExpression(assignment);
        switch (use.Parent)
        {
            case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, use):
                if (member.Parent is InvocationExpressionSyntax invocation
                    && ScopeWalker.IsSame(invocation.Expression, member))
                {
                    return member.Name.Identifier.ValueText == nameof(IDisposable.Dispose)
                           || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                               is IMethodSymbol { ReducedFrom: not null };
                }

                return member.Name.Identifier.ValueText == nameof(IDisposable.Dispose)
                       && member.Parent is not InvocationExpressionSyntax;

            case ElementAccessExpressionSyntax element when ScopeWalker.IsSame(element.Expression, use):
                return false;

            case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, use):
                return conditional.WhenNotNull.DescendantNodesAndSelf()
                    .OfType<MemberBindingExpressionSyntax>()
                    .Any(binding => binding.Name.Identifier.ValueText == nameof(IDisposable.Dispose)
                                    && binding.Parent is InvocationExpressionSyntax invocation
                                    && ScopeWalker.IsSame(invocation.Expression, binding));

            case AssignmentExpressionSyntax outerAssignment when ScopeWalker.IsSame(outerAssignment.Left, use):
            case ExpressionStatementSyntax:
            case ForStatementSyntax:
                return false;

            case AssignmentExpressionSyntax outerAssignment when ScopeWalker.IsSame(outerAssignment.Right, use):
                return context.SemanticModel.GetOperation(
                    outerAssignment.Left, context.CancellationToken) is not IDiscardOperation;

            default:
                // using, return, argument, or assignment source transfers or disposes ownership.
                return true;
        }
    }
}
