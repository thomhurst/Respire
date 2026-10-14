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
        helpLinkUri: "https://thomhurst.github.io/Respire/docs/guides/async-only");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

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
        if (operand is null) return;
        for (var parent = context.Operation.Parent; parent is not null; parent = parent.Parent)
            if (parent is INameOfOperation) return;

        if (context.Operation.SemanticModel is { } model
            && IsRespireOperation(operand, model, unchangedLocals, context.CancellationToken, 0))
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation()));
    }

    internal static IOperation? GetAwaitable(IOperation operation) => operation switch
    {
        IPropertyReferenceOperation { Property.Name: "Result", Instance: { } instance } property
            when IsTask(property.Property.ContainingType) => instance,
        IInvocationOperation { TargetMethod.Name: "Wait", Instance: { } instance } call
            when IsTask(call.TargetMethod.ContainingType) => instance,
        IInvocationOperation { TargetMethod.Name: "GetResult", Arguments.Length: 0,
            Instance: IInvocationOperation { TargetMethod.Name: "GetAwaiter", Arguments.Length: 0, Instance: { } instance } awaiter }
            when IsAwaitable(awaiter.TargetMethod.ContainingType) => instance,
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
        if (depth > 16) return false;
        switch (operation)
        {
            case IConversionOperation conversion when conversion.OperatorMethod is null:
                return IsRespireOperation(conversion.Operand, model, unchangedLocals, cancellationToken, depth + 1);
            case IParenthesizedOperation parentheses:
                return IsRespireOperation(parentheses.Operand, model, unchangedLocals, cancellationToken, depth + 1);
            case IInvocationOperation call:
                if (IsTask(call.Type) && IsRespireMethod(call.TargetMethod)) return true;
                return call.TargetMethod.Name is "AsTask" or "ConfigureAwait" or "WaitAsync"
                    && IsTask(call.TargetMethod.ContainingType) && call.Instance is { } instance
                    && IsRespireOperation(instance, model, unchangedLocals, cancellationToken, depth + 1);
            case ILocalReferenceOperation local:
                if (local.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
                    is not VariableDeclaratorSyntax { Initializer.Value: { } initializer } declaration) return false;
                if (initializer.SyntaxTree != model.SyntaxTree) return false;
                if (!unchangedLocals.TryGetValue(local.Local, out var unchanged))
                {
                    unchanged = IsUnchangedLocal(local.Local, declaration, model, cancellationToken);
                    unchangedLocals.TryAdd(local.Local, unchanged);
                }
                if (!unchanged) return false;
                return model.GetOperation(initializer, cancellationToken) is { } value
                    && IsRespireOperation(value, model, unchangedLocals, cancellationToken, depth + 1);
            default:
                return false;
        }
    }

    private static bool IsUnchangedLocal(ILocalSymbol local, VariableDeclaratorSyntax declaration,
        SemanticModel model, CancellationToken cancellationToken)
    {
        // Cache only mutation checks, not depth-dependent alias traversal results.
        var scope = declaration.Ancestors().FirstOrDefault(n => n is BlockSyntax or GlobalStatementSyntax);
        if (scope is null) return false;
        if (scope is GlobalStatementSyntax) scope = scope.Parent!;
        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identifier.Identifier.ValueText != local.Name) continue;
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, local)) continue;
            var node = (SyntaxNode)identifier;
            // Parentheses and tuple elements can wrap a write to the local. Member/index
            // access cannot: a local used as an index is read, not reassigned.
            while (node.Parent is ParenthesizedExpressionSyntax
                || node.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax })
                node = node.Parent is ArgumentSyntax ? node.Parent.Parent! : node.Parent;
            if (node.Parent is RefExpressionSyntax
                || node.Parent is ArgumentSyntax argument && argument.RefKindKeyword.RawKind != 0
                || node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node) return false;
        }
        return true;
    }

    private static bool IsRespireMethod(IMethodSymbol method)
    {
        var ns = method.ContainingNamespace.ToDisplayString();
        if (ns == "Respire" || ns.StartsWith("Respire.", StringComparison.Ordinal)) return true;
        // User-declared interfaces for the shipped command generator are Respire calls too.
        var type = method.ContainingType;
        if (IsCommandInterface(type)) return true;
        return type.AllInterfaces.Where(IsCommandInterface).Any(contract =>
            contract.GetMembers(method.Name).OfType<IMethodSymbol>().Any(member =>
                SymbolEqualityComparer.Default.Equals(
                    type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method.OriginalDefinition)));
    }

    private static bool IsCommandInterface(INamedTypeSymbol type) => type.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString() == "Respire.RespireCommandsAttribute");
}
