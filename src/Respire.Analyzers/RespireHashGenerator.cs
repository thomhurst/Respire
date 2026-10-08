using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Respire.Analyzers;

/// <summary>Generates scalar hash codecs and model key templates without runtime reflection.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class RespireHashGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidModel = new(
        DiagnosticIds.InvalidGeneratedHash, "Unsupported hash model", "{0}", DiagnosticIds.Category,
        DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://thomhurst.github.io/Respire/docs/guides/generated-hash-codecs");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName("Respire.RespireHashAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (attribute, token) => GeneratedModelBuilder.Build((INamedTypeSymbol)attribute.TargetSymbol,
                attribute.Attributes[0], token)).WithTrackingName("RespireHashModels");
        context.RegisterSourceOutput(models, static (output, model) =>
        {
            if (model.Error is not null)
                output.ReportDiagnostic(Diagnostic.Create(InvalidModel,
                    Location.Create(model.Path, model.Span, model.LineSpan), model.Error));
            else
                output.AddSource(model.HintName!, SourceText.From(model.Source!, Encoding.UTF8));
        });
    }
}
