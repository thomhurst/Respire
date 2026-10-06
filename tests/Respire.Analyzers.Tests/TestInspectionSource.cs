using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers.Tests;

internal static class TestInspectionSource
{
    private const string FactoryName = "InspectForTests";
    internal static readonly HashSet<string> Owners = [
        "Respire.Networking.RespireConnection", "Respire.Networking.PendingResponse",
        "Respire.ClientSideCacheCoordinator", "Respire.RespireTransactionBase"];

    internal static Stream OpenResource(string resource)
        => typeof(TestInspectionSource).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded resource: " + resource);

    internal static IEnumerable<(string Path, string Text)> ReadLibrarySources()
    {
        var assembly = typeof(TestInspectionSource).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var path = resource.Replace('\\', '/');
            if (!path.StartsWith("LibrarySource/", StringComparison.Ordinal)) continue;
            using var stream = OpenResource(resource);
            using var reader = new StreamReader(stream);
            yield return (path, reader.ReadToEnd());
        }
    }

    internal static SyntaxNode Parse(string source, bool net10)
    {
        string[] symbols = ["NET", "NETCOREAPP", "NET8_0_OR_GREATER", "NET7_0_OR_GREATER",
            "NET6_0_OR_GREATER", "NET5_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER",
            "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP2_1_OR_GREATER",
            "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP1_0_OR_GREATER"];
        symbols = symbols.Concat(net10 ? ["NET10_0", "NET10_0_OR_GREATER", "NET9_0_OR_GREATER"] : new[] { "NET8_0" }).ToArray();
        return CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview,
            preprocessorSymbols: symbols)).GetRoot();
    }

    internal static string[] FindOwnerSurface(SyntaxNode root)
    {
        var members = new List<string>();
        foreach (var (declaration, name) in FindOwnerDeclarations(root))
        {
            if (declaration is not ClassDeclarationSyntax owner)
                throw new InvalidOperationException($"Unsupported inspection owner kind: {name} ({declaration.Kind()})");
            if (owner.ParameterList is { } primaryConstructor)
                members.Add($"{name} | constructor {owner.Identifier.ValueText}({Parameters(primaryConstructor)})");
            members.AddRange(FindAccessibleMembers(owner, name));
        }
        return members.ToArray();
    }

    private static IEnumerable<string> FindAccessibleMembers(TypeDeclarationSyntax owner, string name, bool inspectionView = false)
    {
        foreach (var member in owner.Members)
        {
            var modifiers = member.Modifiers;
            // Interface implementations and implicit interface members remain callable.
            if (!modifiers.Any(SyntaxKind.PublicKeyword) && !modifiers.Any(SyntaxKind.InternalKeyword)
                && !modifiers.Any(SyntaxKind.ProtectedKeyword) && ExplicitInterface(member) is null
                && !(owner is InterfaceDeclarationSyntax && !modifiers.Any(SyntaxKind.PrivateKeyword))) continue;
            // Borrowed instance state belongs in the designated view. Static members and
            // accessible nested types can bypass that borrowing boundary and require review.
            if (inspectionView && !modifiers.Any(SyntaxKind.StaticKeyword) && !modifiers.Any(SyntaxKind.ConstKeyword)
                && member is not BaseTypeDeclarationSyntax and not DelegateDeclarationSyntax) continue;
            foreach (var key in MemberKeys(member)) yield return name + " | " + key;
            if (member is TypeDeclarationSyntax nested)
            {
                var isView = Owners.Contains(name) && nested.Identifier.ValueText == "TestInspection";
                foreach (var key in FindAccessibleMembers(nested, name + "." + nested.Identifier.ValueText
                    + Arity(nested.TypeParameterList), isView)) yield return key;
            }
        }
    }

    internal static IEnumerable<(BaseTypeDeclarationSyntax Declaration, string Name)> FindOwnerDeclarations(SyntaxNode root)
    {
        foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any()) continue;
            var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
                .Select(space => string.Concat(space.Name.DescendantTokens().Select(token => token.ValueText)));
            var name = string.Join('.', namespaces.Append(declaration.Identifier.ValueText));
            if (Owners.Contains(name)) yield return (declaration, name);
        }
    }

    internal static string[] FindMissingOwners(IEnumerable<SyntaxNode> roots)
        => Owners.Except(roots.SelectMany(FindOwnerDeclarations).Select(owner => owner.Name), StringComparer.Ordinal).ToArray();

    internal static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees)
        => CSharpCompilation.Create("InspectionGuard", trees,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    internal static string[] FindFactoryUses(SyntaxNode root, SemanticModel? semanticModel = null)
    {
        semanticModel ??= CreateCompilation([root.SyntaxTree]).GetSemanticModel(root.SyntaxTree);
        var factoryUses = root.DescendantNodes().OfType<SimpleNameSyntax>()
            .Where(name => name.Identifier.ValueText == FactoryName && !name.Ancestors()
                .OfType<InvocationExpressionSyntax>().Any(call => call.Expression is IdentifierNameSyntax
                    { Identifier.Text: "nameof" } && semanticModel.GetOperation(call) is INameOfOperation))
            .Select(name => $"line {name.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {name.Parent}");
        var constructions = root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Where(creation => IsForbiddenViewConstruction(creation, semanticModel))
            .Select(creation => $"line {creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {creation}");
        return factoryUses.Concat(constructions).ToArray();
    }

    private static bool IsForbiddenViewConstruction(BaseObjectCreationExpressionSyntax creation, SemanticModel semanticModel)
    {
        if (semanticModel.GetTypeInfo(creation).Type is not INamedTypeSymbol
            { Name: "TestInspection", ContainingType: { } owner } || !Owners.Contains(owner.ToDisplayString())) return false;
        var method = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method is null || semanticModel.GetDeclaredSymbol(method) is not { Name: FactoryName } factory
            || !SymbolEqualityComparer.Default.Equals(factory.ContainingType, owner);
    }

    private static ExplicitInterfaceSpecifierSyntax? ExplicitInterface(MemberDeclarationSyntax member)
        => member switch
        {
            MethodDeclarationSyntax method => method.ExplicitInterfaceSpecifier,
            PropertyDeclarationSyntax property => property.ExplicitInterfaceSpecifier,
            IndexerDeclarationSyntax indexer => indexer.ExplicitInterfaceSpecifier,
            EventDeclarationSyntax @event => @event.ExplicitInterfaceSpecifier,
            _ => null
        };

    private static IEnumerable<string> MemberKeys(MemberDeclarationSyntax member)
    {
        var prefix = ExplicitInterface(member) is { } specifier ? TypeText(specifier.Name) + "." : "";
        switch (member)
        {
            case MethodDeclarationSyntax method:
                yield return $"method {prefix}{method.Identifier.ValueText}{Arity(method.TypeParameterList)}({Parameters(method.ParameterList)}) : {TypeText(method.ReturnType)}";
                break;
            case ConstructorDeclarationSyntax constructor:
                yield return $"constructor {constructor.Identifier.ValueText}({Parameters(constructor.ParameterList)})";
                break;
            case PropertyDeclarationSyntax property:
                yield return $"property {prefix}{property.Identifier.ValueText} {AccessorShape(property)} : {TypeText(property.Type)}";
                break;
            case IndexerDeclarationSyntax indexer:
                yield return $"indexer {prefix}this({Parameters(indexer.ParameterList)}) {AccessorShape(indexer)} : {TypeText(indexer.Type)}";
                break;
            case EventDeclarationSyntax @event:
                yield return $"event {prefix}{@event.Identifier.ValueText} : {TypeText(@event.Type)}";
                break;
            case BaseFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                    yield return $"{(field is EventFieldDeclarationSyntax ? "event" : "field")} {variable.Identifier.ValueText} : {TypeText(field.Declaration.Type)}";
                break;
            case TypeDeclarationSyntax type:
                var kind = type.Keyword.ValueText;
                if (type is RecordDeclarationSyntax record)
                    kind = record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record class";
                kind = (type.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ? "readonly " : "")
                    + (type.Modifiers.Any(SyntaxKind.RefKeyword) ? "ref " : "") + kind;
                yield return $"{kind} {type.Identifier.ValueText}{Arity(type.TypeParameterList)}({Parameters(type.ParameterList)})";
                break;
            case EnumDeclarationSyntax enumeration:
                yield return $"enum {enumeration.Identifier.ValueText} : {(enumeration.BaseList is { } bases ? TypeText(bases.Types.Single().Type) : "int")}";
                break;
            case DelegateDeclarationSyntax @delegate:
                yield return $"delegate {@delegate.Identifier.ValueText}{Arity(@delegate.TypeParameterList)}({Parameters(@delegate.ParameterList)}) : {TypeText(@delegate.ReturnType)}";
                break;
            case OperatorDeclarationSyntax @operator:
                yield return $"operator {@operator.OperatorToken.ValueText}({Parameters(@operator.ParameterList)}) : {TypeText(@operator.ReturnType)}";
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                yield return $"conversion {conversion.ImplicitOrExplicitKeyword.ValueText}({Parameters(conversion.ParameterList)}) : {TypeText(conversion.Type)}";
                break;
            default:
                throw new InvalidOperationException("Uninventoried member kind: " + member.Kind());
        }
    }

    private static string Arity(TypeParameterListSyntax? parameters)
        => parameters is null ? "" : "`" + parameters.Parameters.Count;

    private static string AccessorShape(BasePropertyDeclarationSyntax property)
    {
        var accessibility = ExplicitInterface(property) is not null ? "explicit" : Accessibility(property.Modifiers);
        if (accessibility.Length == 0 && property.Parent is InterfaceDeclarationSyntax) accessibility = "public";
        var accessors = property.AccessorList is { } list
            ? string.Join(", ", list.Accessors.OrderBy(accessor => accessor.Keyword.ValueText, StringComparer.Ordinal)
                .Select(accessor => (Accessibility(accessor.Modifiers) is { Length: > 0 } access ? access + " " : "")
                    + accessor.Keyword.ValueText))
            : "get";
        return $"[{accessibility}; {accessors}]";
    }

    private static string Accessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword))
            return modifiers.Any(SyntaxKind.ProtectedKeyword) ? "private protected" : "private";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword))
            return modifiers.Any(SyntaxKind.InternalKeyword) ? "protected internal" : "protected";
        return modifiers.Any(SyntaxKind.InternalKeyword) ? "internal" : "";
    }

    private static string Parameters(BaseParameterListSyntax? list)
        => list is null ? "" : string.Join(", ", list.Parameters.Select(parameter =>
            string.Concat(parameter.Modifiers.Select(modifier => modifier.ValueText + " ")) + TypeText(parameter.Type!)));

    private static string TypeText(SyntaxNode type)
        => type.ReplaceTrivia(type.DescendantTrivia(), (_, _) => default).NormalizeWhitespace().ToFullString();
}
