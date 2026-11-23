using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>Warns when a Respire task is synchronously consumed.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SyncOverAsyncAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.SyncOverAsync,
        "Await Respire operations",
        "Use 'await' instead of synchronously waiting for a Respire operation",
        DiagnosticIds.Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Respire uses asynchronous I/O. Blocking on its tasks can cause thread pool starvation or deadlocks.",
        helpLinkUri: "https://respire.tomlonghurst.dev/docs/guides/async-only");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get;
    } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(startContext =>
        {
            var unchangedLocals = new ConcurrentDictionary<ILocalSymbol, bool>(SymbolEqualityComparer.Default);
            startContext.RegisterOperationAction(operationContext => Analyze(operationContext, unchangedLocals),
                OperationKind.PropertyReference, OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, ConcurrentDictionary<ILocalSymbol, bool> unchangedLocals)
    {
        var operand = GetAwaitable(context.Operation);

        if (operand is null)
        {
            return;
        }

        for (var parent = context.Operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is INameOfOperation)
            {
                return;
            }
        }

        if (context.Operation.SemanticModel is { } model
            && IsRespireOperation(operand, model, unchangedLocals, context.CancellationToken, 0)
            && !HasSuccessfulCompletionGuard(context, model, operand))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation()));
        }
    }

    private static bool HasSuccessfulCompletionGuard(OperationAnalysisContext context, SemanticModel model, IOperation operand)
    {
        while (operand is IInvocationOperation { TargetMethod.Name: "AsTask" or "ConfigureAwait", Instance: { } instance } adapter
               && IsTask(adapter.TargetMethod.ContainingType))
        {
            operand = instance;
        }

        return operand is ILocalReferenceOperation { Local: { RefKind: RefKind.None } local }
            && ScopeWalker.GetEnclosingScope(context.Operation.Syntax) is { } scope
            && ScopeWalker.IsReadAfterSuccessfulCompletion(model, scope, context.Operation, local, context.CancellationToken);
    }

    internal static IOperation? GetAwaitable(IOperation operation) => operation switch
    {
        IPropertyReferenceOperation { Property.Name: "Result", Instance: { } instance } property when IsTask(property.Property.ContainingType) => instance,
        IInvocationOperation { TargetMethod.Name: "Wait", Instance: { } instance } call when IsTask(call.TargetMethod.ContainingType) => instance,
        IInvocationOperation
        {
            TargetMethod.Name: "GetResult", Arguments.Length: 0,
            Instance: IInvocationOperation { TargetMethod.Name: "GetAwaiter", Arguments.Length: 0, Instance: { } instance } awaiter
        } when IsAwaitable(awaiter.TargetMethod.ContainingType) => instance,
        _ => null,
    };

    internal static bool IsTask(ITypeSymbol? type) => type is INamedTypeSymbol named
        && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
        && named.Name is "Task" or "ValueTask";

    private static bool IsAwaitable(ITypeSymbol? type) => IsTask(type)
        || (type is INamedTypeSymbol named
            && named.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices"
            && named.Name is "ConfiguredTaskAwaitable" or "ConfiguredValueTaskAwaitable");

    private static bool IsRespireOperation(IOperation operation, SemanticModel model,
        ConcurrentDictionary<ILocalSymbol, bool> unchangedLocals, CancellationToken cancellationToken, int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Bound alias traversal, including malformed or cyclic initializers.
        if (depth > 16)
        {
            return false;
        }

        switch (operation)
        {
            case IConversionOperation conversion when conversion.OperatorMethod is null:
                return IsRespireOperation(conversion.Operand, model, unchangedLocals, cancellationToken, depth + 1);
            case IParenthesizedOperation parentheses:
                return IsRespireOperation(parentheses.Operand, model, unchangedLocals, cancellationToken, depth + 1);
            case IInvocationOperation call:
                if (IsTask(call.Type) && IsRespireMethod(call.TargetMethod))
                {
                    return true;
                }
                return call.TargetMethod.Name is "AsTask" or "ConfigureAwait" or "WaitAsync"
                    && IsTask(call.TargetMethod.ContainingType) && call.Instance is { } instance
                    && IsRespireOperation(instance, model, unchangedLocals, cancellationToken, depth + 1);
            case ILocalReferenceOperation local:
                if (local.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
                    is not VariableDeclaratorSyntax { Initializer.Value: { } initializer } declaration)
                {
                    return false;
                }
                if (initializer.SyntaxTree != model.SyntaxTree)
                {
                    return false;
                }
                if (!unchangedLocals.TryGetValue(local.Local, out var unchanged))
                {
                    var scope = ScopeWalker.GetEnclosingScope(declaration);
                    unchanged = scope is not null && ScopeWalker.IsUnchangedLocal(scope, local.Local, model, cancellationToken);
                    unchangedLocals.TryAdd(local.Local, unchanged);
                }
                if (!unchanged)
                {
                    return false;
                }
                return model.GetOperation(initializer, cancellationToken) is { } value
                    && IsRespireOperation(value, model, unchangedLocals, cancellationToken, depth + 1);
            default:
                return false;
        }
    }

    private static bool IsRespireMethod(IMethodSymbol method)
    {
        var ns = method.ContainingNamespace.ToDisplayString();

        if (ns == "Respire" || ns.StartsWith("Respire.", StringComparison.Ordinal))
        {
            return true;
        }
        // User-declared interfaces for the shipped command generator are Respire calls too.
        var type = method.ContainingType;

        if (IsCommandInterface(type))
        {
            return true;
        }

        return type.AllInterfaces.Where(IsCommandInterface).Any(contract =>
            contract.GetMembers(method.Name).OfType<IMethodSymbol>().Any(member =>
                SymbolEqualityComparer.Default.Equals(
                    type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method.OriginalDefinition)));
    }

    private static bool IsCommandInterface(INamedTypeSymbol type) => type.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString() == "Respire.RespireCommandsAttribute");
}
