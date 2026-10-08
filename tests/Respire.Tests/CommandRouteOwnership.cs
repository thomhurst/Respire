using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Respire.Tests;

// Syntax discovery deliberately does not inspect telemetry calls or use their count as a baseline.
internal static class CommandRouteOwnership
{
    internal sealed record Member(string Id, string Type, string Signature, string Name,
        bool PublicRoute, bool HasBody, string File, bool IsInterface = false,
        string[]? ImplementedContracts = null, string Framework = "", bool IsStatic = false, string[]? Files = null);
    internal sealed record Surface(string Type, string OwnerType, string[] Members,
        Dictionary<string, string>? Overrides = null, string Contract = "", string[]? AdditionalOwnerTypes = null);
    internal sealed record Boundary(string Name, string Role, string Member, string? Owner, string Contract);
    internal sealed record NonRoute(string Member, string Reason);
    internal sealed record Inventory(Surface[] Surfaces, Boundary[] Boundaries, NonRoute[]? NonRoutes = null);

    internal static Member[] Discover(IEnumerable<(string File, string Source)> files)
    {
        var source = files.ToArray();
        return Configurations.Value.SelectMany(configuration => DiscoverFramework(source, configuration.Framework, configuration.Symbols))
            .ToArray();
    }

    private sealed record Configuration(string Framework, string[] Symbols);
    private static readonly Lazy<Configuration[]> Configurations = new(ReadConfigurations);

    private static Configuration[] ReadConfigurations()
    {
        using var stream = typeof(CommandRouteOwnership).Assembly.GetManifestResourceStream("Respire.Tests.CommandRouteSymbols")
            ?? throw new InvalidOperationException("Core command route symbols are missing; rebuild the test project.");
        using var reader = new StreamReader(stream);
        var rows = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|')).ToArray();
        if (rows.Length != 2 || rows.Any(row => row.Length != 3 || string.IsNullOrWhiteSpace(row[1]) || string.IsNullOrWhiteSpace(row[2]))
            || !rows.Select(row => row[0]).Order().SequenceEqual(new[] { "net10.0", "net8.0" })
            || rows.Select(row => row[1]).Distinct().Count() != 1)
            throw new InvalidOperationException("Core command route symbols must include both frameworks in the same build configuration.");
        return rows.Select(row => new Configuration(row[0], row[2].Split(',', StringSplitOptions.RemoveEmptyEntries))).ToArray();
    }

    private static Member[] DiscoverFramework((string File, string Source)[] files, string framework, string[] symbols)
    {
        var members = new List<Member>();
        var roots = files.Select(file =>
        {
            var tree = CSharpSyntaxTree.ParseText(file.Source,
                new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols), path: file.File);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0)
                throw new InvalidOperationException("Command route source parse failed [" + framework + "]: "
                    + string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
            var root = tree.GetRoot();
            // Extension receivers require distinct member identities; never treat them as ordinary static methods.
            if (root.DescendantNodes().OfType<ExtensionBlockDeclarationSyntax>().Any())
                throw new InvalidOperationException("Command route extension blocks require explicit discovery support ["
                    + framework + "]: " + file.File);
            return (file.File, Root: root);
        }).ToArray();
        var publicTypes = roots.SelectMany(file => file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Where(type => type.Modifiers.Any(SyntaxKind.PublicKeyword)
                || (type.Parent is InterfaceDeclarationSyntax && !type.Modifiers.Any(token =>
                    token.IsKind(SyntaxKind.PrivateKeyword) || token.IsKind(SyntaxKind.InternalKeyword)
                    || token.IsKind(SyntaxKind.ProtectedKeyword))))
            .Select(TypeId).ToHashSet(StringComparer.Ordinal);
        var typesById = roots.SelectMany(file => file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .GroupBy(TypeId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var globalUsings = roots.SelectMany(file => ((CompilationUnitSyntax)file.Root).Usings)
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).ToArray();
        string ResolveContract(TypeSyntax parent, TypeDeclarationSyntax declaration)
        {
            var name = ContractName(parent);
            if (name.StartsWith("global::", StringComparison.Ordinal)) return name["global::".Length..];
            var usings = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings)
                .Concat(((CompilationUnitSyntax)declaration.SyntaxTree.GetRoot()).Usings).Concat(globalUsings).ToArray();
            foreach (var directive in usings.Where(u => u.Alias is not null && u.Name is not null))
            {
                var alias = directive.Alias!.Name.Identifier.ValueText;
                if (name == alias || name.StartsWith(alias + ".", StringComparison.Ordinal) || name.StartsWith(alias + "::", StringComparison.Ordinal))
                    return ContractName(directive.Name!).Replace("global::", "", StringComparison.Ordinal)
                        + name[alias.Length..].Replace("::", ".", StringComparison.Ordinal);
            }
            var space = Namespace(declaration);
            while (true)
            {
                var candidate = space.Length == 0 ? name : space + "." + name;
                if (typesById.ContainsKey(candidate)) return candidate;
                var separator = space.LastIndexOf('.');
                if (space.Length == 0) break;
                space = separator < 0 ? "" : space[..separator];
            }
            foreach (var directive in usings.Where(u => u.Alias is null && u.Name is not null && !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)))
            {
                var candidate = Compact(directive.Name!).Replace("global::", "", StringComparison.Ordinal) + "." + name;
                if (typesById.ContainsKey(candidate)) return candidate;
            }
            var currentSpace = Namespace(declaration);
            return name.Contains('.') || currentSpace.Length == 0 ? name : currentSpace + "." + name;
        }
        var contractCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] Contracts(string type)
        {
            if (contractCache.TryGetValue(type, out var cached)) return cached;
            var result = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string current)
            {
                if (!typesById.TryGetValue(current, out var declarations)) return;
                foreach (var declaration in declarations)
                    foreach (var parent in declaration.BaseList?.Types ?? [])
                    {
                        var qualified = ResolveContract(parent.Type, declaration);
                        if (result.Add(qualified)) Visit(qualified);
                    }
            }
            Visit(type);
            return contractCache[type] = result.ToArray();
        }
        string[] Reimplemented(string type) => (typesById.GetValueOrDefault(type) ?? [])
            .SelectMany(declaration => (declaration.BaseList?.Types ?? []).Select(parent => ResolveContract(parent.Type, declaration)))
            .Where(contract => typesById.TryGetValue(contract, out var declarations) && declarations.Any(d => d is InterfaceDeclarationSyntax))
            .SelectMany(contract => Contracts(contract).Prepend(contract)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var (file, root) in roots)
        {
            // Merge partial declarations only within this target framework, independently of telemetry.
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var types = method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().ToArray();
                if (types.Length == 0) continue;
                var type = TypeId(types[^1]);
                var explicitContract = method.ExplicitInterfaceSpecifier?.Name.ToString();
                var explicitContractId = method.ExplicitInterfaceSpecifier is null ? null : ContractName(method.ExplicitInterfaceSpecifier.Name);
                var signature = (explicitContract is null ? "" : explicitContract + ".") + method.Identifier.ValueText + Arity(method.TypeParameterList) + "(" +
                    string.Join(",", method.ParameterList.Parameters.Select(Parameter)) + "):" + Compact(method.ReturnType);
                var visible = types.All(t => publicTypes.Contains(TypeId(t)));
                var publicMethod = method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                    (types[^1] is InterfaceDeclarationSyntax && !method.Modifiers.Any(SyntaxKind.PrivateKeyword)
                        && !method.Modifiers.Any(SyntaxKind.InternalKeyword) && !method.Modifiers.Any(SyntaxKind.ProtectedKeyword));
                var route = visible && publicMethod;
                var contracts = Contracts(type);
                // Interface dispatch reaches an explicit implementation only for its named contract, and a
                // hiding method only for interfaces its type re-implements; TreatWarningsAsErrors makes `new` mandatory.
                var implemented = explicitContractId is not null
                    ? contracts.Where(c => c == explicitContractId || c.EndsWith("." + explicitContractId, StringComparison.Ordinal)).ToArray()
                    : !publicMethod ? []
                    : method.Modifiers.Any(SyntaxKind.NewKeyword) ? Reimplemented(type)
                    : contracts;
                members.Add(new(type + "." + signature, type, signature, method.Identifier.ValueText,
                    route, method.Body is not null || method.ExpressionBody is not null, file,
                    types[^1] is InterfaceDeclarationSyntax, implemented, framework,
                    method.Modifiers.Any(SyntaxKind.StaticKeyword)));
            }
            // Deferred inspection and borrowed payload state also have property boundaries.
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var type = property.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (type is null) continue;
                var signature = property.Identifier.ValueText + ":" + Compact(property.Type);
                var typeId = TypeId(type);
                members.Add(new(typeId + "." + signature, typeId, signature, property.Identifier.ValueText,
                    false, property.ExpressionBody is not null || property.AccessorList?.Accessors.Any(a =>
                        a.Body is not null || a.ExpressionBody is not null) == true, file, Framework: framework));
            }
        }
        return members.GroupBy(m => m.Id).Select(g => g.First() with {
            PublicRoute = g.Any(m => m.PublicRoute), HasBody = g.Any(m => m.HasBody), IsStatic = g.Any(m => m.IsStatic),
            ImplementedContracts = g.Where(m => m.HasBody).SelectMany(m => m.ImplementedContracts ?? [])
                .Distinct(StringComparer.Ordinal).ToArray(),
            // Partial declaration and implementation files are both valid homes for an ownership declaration.
            Files = g.Select(m => m.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        }).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
    }

    private static string TypeName(TypeDeclarationSyntax type) => type.Identifier.ValueText + Arity(type.TypeParameterList);
    private static string ContractName(TypeSyntax type) => type switch
    {
        GenericNameSyntax generic => generic.Identifier.ValueText + "`" + generic.TypeArgumentList.Arguments.Count,
        QualifiedNameSyntax qualified => ContractName(qualified.Left) + "." + ContractName(qualified.Right),
        AliasQualifiedNameSyntax alias => alias.Alias.Identifier.ValueText + "::" + ContractName(alias.Name),
        _ => Compact(type)
    };
    private static string TypeId(TypeDeclarationSyntax type)
    {
        var space = Namespace(type);
        return (space.Length == 0 ? "" : space + ".") + string.Join(".", type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(TypeName));
    }
    private static string Namespace(TypeDeclarationSyntax type) => string.Join(".", type.Ancestors()
        .OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
    private static string Arity(TypeParameterListSyntax? list) => list is null ? "" : "`" + list.Parameters.Count;
    private static string Compact(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
    private static string Parameter(ParameterSyntax parameter) => string.Concat(parameter.Modifiers.Select(t => t.Text + " "))
        + (parameter.Type is null ? "" : Compact(parameter.Type));

    internal static string[] Validate(Member[] source, Inventory inventory)
    {
        if (source.Length == 0) return ValidateFramework(source, inventory);
        var publicIds = source.Where(m => m.PublicRoute).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var ids = source.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var duplicate in inventory.Boundaries.GroupBy(b => b.Name).Where(group => group.Count() > 1))
            errors.Add("Duplicate boundary: " + duplicate.Key);
        foreach (var framework in source.GroupBy(m => m.Framework))
        {
            var members = framework.ToArray();
            var frameworkPublicIds = members.Where(m => m.PublicRoute).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var frameworkIds = members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            // A declaration can cover a target-specific route or boundary. Missing declarations
            // still fail globally; executable owners and inherited contracts stay target-specific.
            bool IncludesRoute(string id) => frameworkPublicIds.Contains(id) || !publicIds.Contains(id);
            var surfaces = inventory.Surfaces.Select(surface => surface with {
                Members = surface.Members.Where(signature => IncludesRoute(surface.Type + "." + signature)).ToArray(),
                Overrides = surface.Overrides?.Where(pair => !surface.Members.Contains(pair.Key)
                    || IncludesRoute(surface.Type + "." + pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)
            }).ToArray();
            var targetInventory = inventory with {
                Surfaces = surfaces,
                Boundaries = inventory.Boundaries.Where(b => frameworkIds.Contains(b.Member) || !ids.Contains(b.Member)).ToArray(),
                NonRoutes = inventory.NonRoutes?.Where(n => IncludesRoute(n.Member)).ToArray()
            };
            errors.AddRange(ValidateFramework(members, targetInventory).Select(error => error + " [" + framework.Key + "]"));
        }
        return errors.ToArray();
    }

    private static bool Implements(Member owner, Member contract) => owner.IsStatic == contract.IsStatic
        && (owner.ImplementedContracts ?? []).Contains(contract.Type, StringComparer.Ordinal);

    private static string[] ValidateFramework(Member[] source, Inventory inventory)
    {
        var errors = new List<string>();
        var members = source.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in inventory.Surfaces)
        {
            if (string.IsNullOrWhiteSpace(surface.Contract)) errors.Add("Missing surface lifetime contract: " + surface.Type);
            foreach (var signature in surface.Members)
            {
                var route = surface.Type + "." + signature;
                if (!declared.Add(route)) errors.Add("Duplicate route: " + route);
                if (!members.TryGetValue(route, out var entry) || !entry.PublicRoute)
                    errors.Add("Removed or non-public route: " + route);
                var owner = surface.Overrides?.GetValueOrDefault(signature) ?? surface.OwnerType + "." + signature;
                if (!members.TryGetValue(owner, out var final) || !final.HasBody)
                    errors.Add("Missing final owner: " + route + " => " + owner);
                else if (entry?.IsInterface == true && entry.Id != final.Id && final.Signature == entry.Signature
                    && !Implements(final, entry))
                    errors.Add("Owner does not implement public contract: " + route + " => " + owner);
                foreach (var additionalType in surface.AdditionalOwnerTypes ?? [])
                {
                    var additionalOwner = additionalType + "." + signature;
                    if (!members.TryGetValue(additionalOwner, out var additional) || !additional.HasBody)
                        errors.Add("Missing alternative final owner: " + route + " => " + additionalOwner);
                    else if (entry?.IsInterface == true && !Implements(additional, entry))
                        errors.Add("Alternative owner does not implement public contract: " + route + " => " + additionalOwner);
                }
            }
            if (surface.Overrides is not null)
                foreach (var signature in surface.Overrides.Keys.Except(surface.Members))
                    errors.Add("Orphan owner override: " + surface.Type + "." + signature);
        }
        foreach (var exclusion in inventory.NonRoutes ?? [])
        {
            if (!declared.Add(exclusion.Member)) errors.Add("Duplicate route classification: " + exclusion.Member);
            if (!members.TryGetValue(exclusion.Member, out var member) || !member.PublicRoute)
                errors.Add("Removed or non-public exclusion: " + exclusion.Member);
            if (string.IsNullOrWhiteSpace(exclusion.Reason)) errors.Add("Missing non-route reason: " + exclusion.Member);
        }
        foreach (var route in source.Where(m => m.PublicRoute && !declared.Contains(m.Id)))
            errors.Add("Undeclared public route: " + route.Id);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundary in inventory.Boundaries)
        {
            if (!names.Add(boundary.Name)) errors.Add("Duplicate boundary: " + boundary.Name);
            if (!members.ContainsKey(boundary.Member)) errors.Add("Missing boundary member: " + boundary.Name);
            if (string.IsNullOrWhiteSpace(boundary.Contract)) errors.Add("Missing lifetime contract: " + boundary.Name);
            if (boundary.Role is not ("final" or "helper" or "borrower" or "internal"))
                errors.Add("Unknown ownership role: " + boundary.Name);
            if (boundary.Role == "internal")
            {
                if (boundary.Owner is not null) errors.Add("Internal observation cannot publish caller failure: " + boundary.Name);
            }
            else if (boundary.Owner is null || !members.TryGetValue(boundary.Owner, out var owner) || !owner.HasBody)
                errors.Add("Missing boundary final owner: " + boundary.Name);
            if (boundary.Role == "final" && boundary.Member != boundary.Owner)
                errors.Add("Final boundary must own itself: " + boundary.Name);
            if (boundary.Role is "helper" or "borrower" && boundary.Member == boundary.Owner)
                errors.Add("Borrowed/helper boundary cannot own itself: " + boundary.Name);
        }
        return errors.ToArray();
    }

    internal static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "Respire", "Respire.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Command route guard requires the repository source checkout.");
    }
}
