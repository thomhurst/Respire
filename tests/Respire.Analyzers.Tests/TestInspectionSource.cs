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
    private static readonly MetadataReference[] References = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Missing runtime assembly references."))
        // Bind the embedded library definitions, never their already-built counterparts.
        .Split(Path.PathSeparator).Where(path => !Path.GetFileName(path).StartsWith("Respire", StringComparison.Ordinal))
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    internal sealed record SourceConfiguration(string Framework, string[] Symbols);

    /// <summary>Reads the SDK-evaluated framework and symbol manifest embedded at build time.</summary>
    internal static SourceConfiguration[] ReadSourceConfigurations()
    {
        using var stream = OpenResource("TestInspectionConfigurations.txt");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var parts = line.Trim().Split('|');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                throw new InvalidOperationException("Invalid source configuration: " + line);
            return new SourceConfiguration(parts[0], parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }).ToArray();
    }

    /// <summary>Opens a required test resource and reports its name when missing.</summary>
    internal static Stream OpenResource(string resource)
        => typeof(TestInspectionSource).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded resource: " + resource);

    /// <summary>Reads production source independently of a repository checkout.</summary>
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

    /// <summary>Selects an SDK-derived framework configuration for self-contained regression controls.</summary>
    internal static SyntaxNode Parse(string source, bool net10)
        => Parse(source, ReadSourceConfigurations().Single(configuration => configuration.Framework == (net10 ? "net10.0" : "net8.0")));

    /// <summary>Parses source under the evaluated symbols of one production framework.</summary>
    internal static SyntaxNode Parse(string source, SourceConfiguration configuration)
        => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview,
            preprocessorSymbols: configuration.Symbols)).GetRoot();

    /// <summary>Inventories reviewable declarations on inspection owners and their privileged subclasses.</summary>
    internal static string[] FindOwnerSurface(SyntaxNode root, SemanticModel? semanticModel = null)
    {
        semanticModel ??= CreateCompilation([root.SyntaxTree]).GetSemanticModel(root.SyntaxTree);
        var members = new List<string>();
        var declarations = FindOwnerDeclarations(root).Concat(FindDerivedDeclarations(root, semanticModel));
        foreach (var (declaration, name) in declarations)
        {
            if (declaration is not ClassDeclarationSyntax owner)
                throw new InvalidOperationException($"Unsupported inspection owner kind: {name} ({declaration.Kind()})");
            if (owner.ParameterList is { } primaryConstructor)
                members.Add($"{name} | constructor {owner.Identifier.ValueText}({Parameters(primaryConstructor)})");
            members.AddRange(FindAccessibleMembers(owner, name));
        }
        return members.ToArray();
    }

    /// <summary>Discovers direct and indirect subclasses while avoiding duplicate nested-type scans.</summary>
    private static IEnumerable<(BaseTypeDeclarationSyntax Declaration, string Name)> FindDerivedDeclarations(SyntaxNode root, SemanticModel semanticModel)
    {
        foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(declaration) is not { } symbol || !HasOwnerBase(symbol)) continue;
            // Nested members of an inventoried type are already visited recursively.
            if (declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any(parent =>
                semanticModel.GetDeclaredSymbol(parent) is { } containing
                && (Owners.Contains(containing.ToDisplayString()) || HasOwnerBase(containing)))) continue;
            yield return (declaration, symbol.ToDisplayString());
        }
    }

    /// <summary>Determines whether a type inherits privileged access to a designated owner's state.</summary>
    private static bool HasOwnerBase(INamedTypeSymbol symbol)
    {
        for (var parent = symbol.BaseType; parent is not null; parent = parent.BaseType)
            if (Owners.Contains(parent.ToDisplayString())) return true;
        return false;
    }

    /// <summary>Recursively inventories callable members while permitting borrowed instance view state.</summary>
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

    /// <summary>Finds the exact qualified owners without accepting similarly named unrelated types.</summary>
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

    /// <summary>Reports required owners absent from the parsed source set.</summary>
    internal static string[] FindMissingOwners(IEnumerable<SyntaxNode> roots)
        => Owners.Except(roots.SelectMany(FindOwnerDeclarations).Select(owner => owner.Name), StringComparer.Ordinal).ToArray();

    /// <summary>Binds source against runtime dependencies, excluding built copies of the inspected libraries.</summary>
    internal static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees)
        => CSharpCompilation.Create("InspectionGuard", trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>Rejects missing views and views resolved from an assembly other than the source compilation.</summary>
    internal static void RequireResolvedViews(CSharpCompilation compilation, IEnumerable<string>? owners = null)
    {
        foreach (var owner in owners ?? Owners)
        {
            if (compilation.GetTypeByMetadataName(owner + "+TestInspection") is not
                { TypeKind: TypeKind.Struct, IsRefLikeType: true } view
                || !SymbolEqualityComparer.Default.Equals(view.ContainingAssembly, compilation.Assembly))
                throw new InvalidOperationException("Unresolved designated inspection view: " + owner + ".TestInspection");
        }
    }

    /// <summary>Reports executable factory references and forbidden construction while permitting bound metadata uses.</summary>
    internal static string[] FindFactoryUses(SyntaxNode root, SemanticModel? semanticModel = null)
    {
        // Single-tree compilation is for self-contained controls. Production passes a
        // model from the whole source set after RequireResolvedViews has succeeded.
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

    /// <summary>Allows construction only inside the exact designated factory of the matching owner.</summary>
    private static bool IsForbiddenViewConstruction(BaseObjectCreationExpressionSyntax creation, SemanticModel semanticModel)
    {
        if (semanticModel.GetTypeInfo(creation).Type is not INamedTypeSymbol
            { Name: "TestInspection", ContainingType: { } owner } view || !Owners.Contains(owner.ToDisplayString())) return false;
        var method = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method is null || semanticModel.GetDeclaredSymbol(method) is not
            { Name: FactoryName, IsStatic: false, Arity: 0, Parameters.Length: 0, DeclaredAccessibility: Microsoft.CodeAnalysis.Accessibility.Internal } factory
            || !SymbolEqualityComparer.Default.Equals(factory.ReturnType, view)
            || !SymbolEqualityComparer.Default.Equals(factory.ContainingType, owner);
    }

    /// <summary>Finds interface implementations callable despite lacking accessibility modifiers.</summary>
    private static ExplicitInterfaceSpecifierSyntax? ExplicitInterface(MemberDeclarationSyntax member)
        => member switch
        {
            MethodDeclarationSyntax method => method.ExplicitInterfaceSpecifier,
            PropertyDeclarationSyntax property => property.ExplicitInterfaceSpecifier,
            IndexerDeclarationSyntax indexer => indexer.ExplicitInterfaceSpecifier,
            EventDeclarationSyntax @event => @event.ExplicitInterfaceSpecifier,
            _ => null
        };

    /// <summary>Produces stable declaration keys without incorporating implementation bodies or parameter names.</summary>
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

    /// <summary>Records generic overload shape without including constraints.</summary>
    private static string Arity(TypeParameterListSyntax? parameters)
        => parameters is null ? "" : "`" + parameters.Parameters.Count;

    /// <summary>Preserves getter, setter, initializer and accessibility changes while ignoring accessor bodies.</summary>
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

    /// <summary>Normalizes combined accessibility modifiers for reviewed property signatures.</summary>
    private static string Accessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword))
            return modifiers.Any(SyntaxKind.ProtectedKeyword) ? "private protected" : "private";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword))
            return modifiers.Any(SyntaxKind.InternalKeyword) ? "protected internal" : "protected";
        return modifiers.Any(SyntaxKind.InternalKeyword) ? "internal" : "";
    }

    /// <summary>Records parameter types and passing modifiers while excluding parameter names and defaults.</summary>
    private static string Parameters(BaseParameterListSyntax? list)
        => list is null ? "" : string.Join(", ", list.Parameters.Select(parameter =>
            string.Concat(parameter.Modifiers.Select(modifier => modifier.ValueText + " ")) + TypeText(parameter.Type!)));

    /// <summary>Normalizes type syntax without whitespace or comments affecting inventory keys.</summary>
    private static string TypeText(SyntaxNode type)
        => type.ReplaceTrivia(type.DescendantTrivia(), (_, _) => default).NormalizeWhitespace().ToFullString();
}
