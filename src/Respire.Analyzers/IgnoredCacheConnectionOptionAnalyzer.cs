using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>Warns about explicit connection options on an adapter that already has a client.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IgnoredCacheConnectionOptionAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.IgnoredCacheConnectionOption,
        title: "Distributed-cache adapter ignores connection option",
        messageFormat: "'{0}' is ignored by AsDistributedCache because it uses the supplied client",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "AsDistributedCache adapts an existing client. ConnectionString and ClientOptions "
            + "cannot create or replace that client's connection; configure the client before adapting it.",
        helpLinkUri: "https://thomhurst.github.io/Respire/docs/integrations/caching#ignored-connection-options-resp004");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static compilation =>
        {
            var accessor = compilation.Compilation.GetTypeByMetadataName(
                "Respire.Extensions.Caching.RespireDistributedCacheClientExtensions");
            var options = compilation.Compilation.GetTypeByMetadataName("Respire.Extensions.Caching.RespireCacheOptions");
            if (accessor is null || options is null) return;
            compilation.RegisterOperationAction(operation => AnalyzeInvocation(operation, accessor, options), OperationKind.Invocation);
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol accessor, INamedTypeSymbol options)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (method.Name != "AsDistributedCache" || !method.IsExtensionMethod
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, accessor)) return;

        foreach (var argument in invocation.Arguments)
        {
            if (!SymbolEqualityComparer.Default.Equals(argument.Parameter?.Type, options)
                || Unwrap(argument.Value) is not IObjectCreationOperation { Initializer: { } initializer } creation
                || !SymbolEqualityComparer.Default.Equals(creation.Type, options)) continue;

            // Deliberately inspect only a directly supplied initializer. Shared options,
            // factories and conditional values can have callers whose intent we cannot infer.
            foreach (var member in initializer.Initializers)
            {
                if (member is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation property } assignment
                    || !SymbolEqualityComparer.Default.Equals(property.Property.ContainingType, options)
                    || property.Property.Name is not ("ConnectionString" or "ClientOptions")
                    || (assignment.Value.ConstantValue.HasValue && assignment.Value.ConstantValue.Value is null)) continue;

                context.ReportDiagnostic(Diagnostic.Create(Rule, property.Syntax.GetLocation(), property.Property.Name));
            }
        }
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation { OperatorMethod: null } conversion: operation = conversion.Operand; break;
                case IParenthesizedOperation parentheses: operation = parentheses.Operand; break;
                default: return operation;
            }
        }
    }
}
