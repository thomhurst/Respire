using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>
/// RESP001: a <c>RespireResult</c> or <c>RespireLease</c> from an explicit lease API dropped
/// without disposal leaks its pooled buffer.
/// </summary>
/// <remarks>
/// The analysis tracks named locals and reports temporary leases used as discards or non-owning reads.
/// Passing the lease itself to another owner, such as through an argument or return, is treated as an
/// ownership transfer. Indexing and enumeration borrow views of the elements and leave the root
/// lease's disposal obligation with its owner.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedPooledResultAnalyzer : DiagnosticAnalyzer
{
    internal const string ResultTypeName = "Respire.RespireResult";
    internal const string LeaseTypeName = "Respire.RespireLease";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.UndisposedPooledResult,
        title: "Pooled Respire result is never disposed",
        messageFormat: "{0} holds a pooled {1} that is never disposed; use 'using' or dispose it explicitly",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Results from explicit lease APIs own pooled memory. "
            + "Dispose the root lease explicitly or with 'using' to return the buffer to the pool.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get;
    } = ImmutableArray.Create(Rule);

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
            compilationStart.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeTemporary(nodeContext, resultType, leaseType),
                SyntaxKind.AwaitExpression);
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

            if (FindAcquisitions(context, variable.Initializer.Value).All(acquisition =>
                    IsDisposedOrEscapes(context, scope, acquisition, acquisitionAssignment: null, local)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), $"'{local.Name}'", pooledType.Name));
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

        if (pooledType is null
            || FindAcquisitions(context, assignment.Right).All(acquisition =>
                IsDisposedOrEscapes(context, scope, acquisition, assignment, local)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), $"'{local.Name}'", pooledType.Name));
    }

    private static void AnalyzeTemporary(
        SyntaxNodeAnalysisContext context, INamedTypeSymbol? resultType, INamedTypeSymbol? leaseType)
    {
        var awaited = (AwaitExpressionSyntax)context.Node;

        if (context.SemanticModel.GetTypeInfo(awaited, context.CancellationToken).Type is not { } type
            || (Match(type, resultType) ?? Match(type, leaseType)) is not { } pooledType
            || !IsRespireAcquisition(context, awaited.Expression)
            || IsImmediatelyReleasedOrTransferred(context, GetOutermostAcquisitionExpression(context, awaited)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, awaited.GetLocation(), "This expression", pooledType.Name));
    }

    private static ExpressionSyntax GetOutermostAcquisitionExpression(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        while (true)
        {
            expression = GetOutermostLeaseValue(context, expression);

            switch (expression.Parent)
            {
                case ConditionalExpressionSyntax conditional when ScopeWalker.IsSame(conditional.WhenTrue, expression)
                                                                 || ScopeWalker.IsSame(conditional.WhenFalse, expression):
                    expression = conditional;
                    break;
                case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                    expression = coalesce;
                    break;
                case SwitchExpressionArmSyntax arm when ScopeWalker.IsSame(arm.Expression, expression)
                                                        && arm.Parent is SwitchExpressionSyntax switchExpression:
                    expression = switchExpression;
                    break;
                default:
                    return expression;
            }
        }
    }

    private static ExpressionSyntax GetOutermostLeaseValue(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        while (true)
        {
            expression = ScopeWalker.GetOutermostTransparentExpression(expression);
            var wrapper = ScopeWalker.GetValuePreservingWrapper(
                expression, context.SemanticModel, context.CancellationToken);

            if (wrapper is null)
            {
                return expression;
            }

            expression = wrapper;
        }
    }

    private static bool ContainsRespireAcquisition(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        => FindAcquisitions(context, expression).Any();

    private static IEnumerable<AwaitExpressionSyntax> FindAcquisitions(
        SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        expression = ScopeWalker.Unwrap(expression);

        while (ScopeWalker.GetPreservedValueOperand(expression, context.SemanticModel, context.CancellationToken) is { } operand)
        {
            expression = ScopeWalker.Unwrap(operand);
        }

        switch (expression)
        {
            case AwaitExpressionSyntax awaited when IsRespireAcquisition(context, awaited.Expression):
                yield return awaited;
                break;
            case ConditionalExpressionSyntax conditional:
                foreach (var acquisition in FindAcquisitions(context, conditional.WhenTrue)
                             .Concat(FindAcquisitions(context, conditional.WhenFalse)))
                {
                    yield return acquisition;
                }
                break;
            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var acquisition in FindAcquisitions(context, coalesce.Left)
                             .Concat(FindAcquisitions(context, coalesce.Right)))
                {
                    yield return acquisition;
                }
                break;
            case SwitchExpressionSyntax switchExpression:
                foreach (var arm in switchExpression.Arms)
                {
                    foreach (var acquisition in FindAcquisitions(context, arm.Expression))
                    {
                        yield return acquisition;
                    }
                }
                break;
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
            "ExecuteLeaseAsync" or "ExecuteLeaseSpanAsync" => containingType is "Respire.RespireClient"
                or "Respire.IRespireClient"
                or "Respire.ScriptCommands"
                or "Respire.IRespireScriptCommands"
                or "Respire.FunctionCommands"
                or "Respire.IRespireFunctionCommands",
            "GetLeaseAsync" => containingType is "Respire.RespireClient"
                or "Respire.IRespireClient"
                or "Respire.StringCommands"
                or "Respire.IRespireStringCommands",
            _ => false,
        };
    }

    private static bool IsAwaitAdapter(IMethodSymbol method) => method.Name switch
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

        if (acquisitionAssignment is not null)
        {
            var use = GetOutermostLeaseValue(context, acquisitionAssignment);

            if (IsImmediatelyReleasedOrTransferred(context, use))
            {
                if (use.Parent is not AssignmentExpressionSyntax)
                {
                    return true;
                }

                // A store can fail after the inner assignment has acquired the lease.
                releases.Add(acquisitionAssignment);
            }
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

            var use = GetOutermostLeaseValue(context, reference);

            if (use.Parent is AssignmentExpressionSyntax discard && ScopeWalker.IsSame(discard.Right, use)
                && IsDiscardTarget(context, discard))
            {
                continue;
            }

            if (IsReassignedBefore(context, scope, acquisition, reference, reassignments))
            {
                // Later uses refer to the replacement, not the acquired pooled owner.
                continue;
            }

            if (ScopeWalker.IsNestedInLambda(reference, scope))
            {
                releases.Add(reference);
                continue;
            }

            switch (use.Parent)
            {
                // result.AsString(), result.Type, result.Dispose()
                case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, use):
                    if (GetMemberRelease(context, member) is { } release)
                    {
                        releases.Add(release);
                    }

                    break;

                // Indexing and enumeration borrow the elements without disposing the root.
                case ElementAccessExpressionSyntax element when ScopeWalker.IsSame(element.Expression, use):
                case CommonForEachStatementSyntax loop when ScopeWalker.IsSame(loop.Expression, use):
                    break;

                // result?.Dispose() or result?.Consume()
                case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, use):
                    if (ReleasesConditionalReceiver(context, conditional))
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

                // Overwriting the local or ignoring a wrapper's result leaves the acquired lease unreleased.
                case AssignmentExpressionSyntax assignment when ScopeWalker.IsSame(assignment.Left, use):
                case ExpressionStatementSyntax:
                case ForStatementSyntax:
                    break;

                // Anything else — an argument, a return, or an assignment source — hands ownership away.
                default:
                    // Retain the local reference so flow analysis can locate where transfer actually completes.
                    releases.Add(reference);
                    break;
            }
        }

        return ScopeWalker.CollectivelyPostDominates(
            context.SemanticModel, scope, acquisition, releases, context.CancellationToken);
    }

    private static bool IsReassignedBefore(
        SyntaxNodeAnalysisContext context,
        SyntaxNode scope,
        SyntaxNode acquisition,
        SyntaxNode reference,
        IEnumerable<AssignmentExpressionSyntax> reassignments)
        => reassignments.Any(reassignment =>
            ScopeWalker.CanReachWithoutCrossing(
                context.SemanticModel,
                scope,
                acquisition,
                reassignment,
                [reference],
                context.CancellationToken)
            && ScopeWalker.CanReach(
                context.SemanticModel, scope, reassignment, reference, context.CancellationToken));

    private static bool IsImmediatelyReleasedOrTransferred(
        SyntaxNodeAnalysisContext context, ExpressionSyntax use)
    {
        switch (use.Parent)
        {
            case MemberAccessExpressionSyntax member when ScopeWalker.IsSame(member.Expression, use):
                return GetMemberRelease(context, member) is not null;

            case ElementAccessExpressionSyntax element when ScopeWalker.IsSame(element.Expression, use):
            case CommonForEachStatementSyntax loop when ScopeWalker.IsSame(loop.Expression, use):
                return false;

            case ConditionalAccessExpressionSyntax conditional when ScopeWalker.IsSame(conditional.Expression, use):
                return ReleasesConditionalReceiver(context, conditional);

            case AssignmentExpressionSyntax outerAssignment when ScopeWalker.IsSame(outerAssignment.Left, use):
            case ExpressionStatementSyntax:
            case ForStatementSyntax:
                return false;

            case AssignmentExpressionSyntax outerAssignment when ScopeWalker.IsSame(outerAssignment.Right, use):
                return !IsDiscardTarget(context, outerAssignment);

            default:
                // using, return, argument, or assignment source transfers or disposes ownership.
                return true;
        }
    }

    private static ExpressionSyntax? GetMemberRelease(
        SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax member)
    {
        if (member.Parent is InvocationExpressionSyntax invocation && ScopeWalker.IsSame(invocation.Expression, member))
        {
            return IsReleaseOrTransferInvocation(context, invocation, member.Name) ? invocation : null;
        }

        return member.Name.Identifier.ValueText == nameof(IDisposable.Dispose) ? member : null;
    }

    private static bool ReleasesConditionalReceiver(
        SyntaxNodeAnalysisContext context, ConditionalAccessExpressionSyntax conditional)
    {
        // Follow the receiver chain so a call on a projection or argument cannot release the root.
        ExpressionSyntax? current = conditional.WhenNotNull;

        while (current is not null)
        {
            current = ScopeWalker.Unwrap(current);

            if (current is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax binding } invocation)
            {
                return IsReleaseOrTransferInvocation(context, invocation, binding.Name);
            }

            current = current switch
            {
                InvocationExpressionSyntax call => call.Expression,
                MemberAccessExpressionSyntax member => member.Expression,
                ElementAccessExpressionSyntax element => element.Expression,
                ConditionalAccessExpressionSyntax nested => nested.Expression,
                _ => null,
            };
        }

        return false;
    }

    private static bool IsReleaseOrTransferInvocation(
        SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, SimpleNameSyntax name)
    {
        return name.Identifier.ValueText == nameof(IDisposable.Dispose)
               || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                   is IMethodSymbol { ReducedFrom: not null };
    }

    private static bool IsDiscardTarget(SyntaxNodeAnalysisContext context, AssignmentExpressionSyntax assignment)
        => context.SemanticModel.GetOperation(assignment.Left, context.CancellationToken) is IDiscardOperation;
}
