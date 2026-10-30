using System.Collections.Generic;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TypeShapeAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ShapeDescriptors.PublicCoreType, ShapeDescriptors.UnsealedClass, ShapeDescriptors.PublicSetterInModel,
        ShapeDescriptors.HybridModelType, ShapeDescriptors.ValueObjectShape, ShapeDescriptors.ContractShape,
        ShapeDescriptors.TechnologyWord, ShapeDescriptors.PersistenceAttributeInModel);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var config = new CompilationConfig(start.Options, start.Compilation);
            var roles = new ShapeRoles(config, start.Compilation);
            if (roles.Skip)
            {
                return;
            }

            start.RegisterSymbolAction(c => AnalyzeType(c, roles), SymbolKind.NamedType);
            start.RegisterCompilationEndAction(c => AnalyzeVisibility(c, roles));
        });

    private static void AnalyzeType(SymbolAnalysisContext context, ShapeRoles roles)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.IsImplicitlyDeclared || !type.Locations.Any(l => l.IsInSource) || type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
        {
            return;
        }

        if (roles.IsModel(type))
        {
            ModelShape.Analyze(context, type, roles);
        }

        if (roles.IsContract(type) && !type.IsStatic && type.TypeKind != TypeKind.Interface)
        {
            ContractShape.Analyze(context, type);
        }

        if (roles.IsPort(type) || roles.IsCore(type))
        {
            TechnologyNames.Analyze(context, type, roles);
        }
    }

    private static void AnalyzeVisibility(CompilationAnalysisContext context, ShapeRoles roles)
    {
        var types = AllTypes(context.Compilation.Assembly.GlobalNamespace).Where(t => !t.IsImplicitlyDeclared && t.Locations.Any(l => l.IsInSource)).ToArray();
        var reachable = PortReachable(types, roles);
        var derived = types.Select(t => t.BaseType?.OriginalDefinition).Where(t => t is not null).ToImmutableHashSet<INamedTypeSymbol?>(SymbolEqualityComparer.Default);
        foreach (var type in types.Where(t => t.ContainingType is null))
        {
            if (IsPublicCoreLeak(type, roles, reachable))
            {
                context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.PublicCoreType, type.Locations[0], type.Name));
            }
        }

        foreach (var type in types.Where(t => t.TypeKind == TypeKind.Class && roles.CoreLayer))
        {
            if (IsSealable(type) && !derived.Contains(type.OriginalDefinition))
            {
                context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.UnsealedClass, type.Locations[0], type.Name));
            }
        }
    }

    private static bool IsPublicCoreLeak(INamedTypeSymbol type, ShapeRoles roles, ImmutableHashSet<INamedTypeSymbol> reachable) =>
        type.DeclaredAccessibility == Accessibility.Public
        && roles.CoreLayer
        && !roles.IsPort(type)
        && !roles.IsContract(type)
        && !reachable.Contains(type)
        && type.Name != "Program"
        && !type.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsExtensionMethod && m.Parameters[0].Type.Name == "IServiceCollection");

    private static bool IsSealable(INamedTypeSymbol type) =>
        !type.IsSealed && !type.IsAbstract && !type.IsStatic
        && !type.GetMembers().Any(m => !m.IsImplicitlyDeclared && !m.IsOverride && (m.IsVirtual || m.IsAbstract || m.DeclaredAccessibility == Accessibility.Protected));

    private static ImmutableHashSet<INamedTypeSymbol> PortReachable(INamedTypeSymbol[] types, ShapeRoles roles)
    {
        var reachable = new System.Collections.Generic.HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new System.Collections.Generic.Queue<INamedTypeSymbol>(types.Where(roles.IsPort));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!reachable.Add(current))
            {
                continue;
            }

            foreach (var used in SignatureTypes(current).SelectMany(TypeNames.Flatten).OfType<INamedTypeSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(used.ContainingAssembly, current.ContainingAssembly))
                {
                    queue.Enqueue(used.OriginalDefinition);
                }
            }
        }

        return reachable.ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    }

    private static IEnumerable<ITypeSymbol> SignatureTypes(INamedTypeSymbol type)
    {
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var implemented in type.Interfaces)
        {
            yield return implemented;
        }

        foreach (var member in type.GetMembers().Where(m => m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected))
        {
            switch (member)
            {
                case IMethodSymbol method:
                    yield return method.ReturnType;
                    foreach (var parameter in method.Parameters)
                    {
                        yield return parameter.Type;
                    }

                    break;
                case IPropertySymbol property:
                    yield return property.Type;
                    break;
                case IFieldSymbol field:
                    yield return field.Type;
                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers().SelectMany(Nested))
        {
            yield return type;
        }

        foreach (var child in ns.GetNamespaceMembers().SelectMany(AllTypes))
        {
            yield return child;
        }
    }

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(Nested));
}

internal static class ModelShape
{
    public static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol type, ShapeRoles roles)
    {
        if (type.TypeKind == TypeKind.Interface)
        {
            return;
        }

        PublicSetters(context, type);
        Hybrid(context, type);
        ValueObject(context, type, roles);
        Attributes(context, type, roles);
    }

    private static void PublicSetters(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared && !m.IsStatic && !m.IsOverride && m.DeclaredAccessibility == Accessibility.Public))
        {
            var mutable = member switch
            {
                IPropertySymbol { SetMethod: { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } } => true,
                IFieldSymbol { IsReadOnly: false, IsConst: false } => true,
                _ => false,
            };
            if (mutable)
            {
                context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.PublicSetterInModel, member.Locations[0], member.Name, type.Name));
            }
        }
    }

    private static void Hybrid(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        var mutable = type.GetMembers().OfType<IPropertySymbol>()
            .Any(p => !p.IsImplicitlyDeclared && !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && p.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false });
        if (mutable && type.GetMembers().OfType<IMethodSymbol>().Any(HasLogic))
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.HybridModelType, type.Locations[0], type.Name));
        }
    }

    private static bool HasLogic(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.IsOverride || method.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        var body = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax syntax
            ? (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody
            : null;
        return body is not null && Metrics.Complexity(body) > 1;
    }

    private static void ValueObject(SymbolAnalysisContext context, INamedTypeSymbol type, ShapeRoles roles)
    {
        if (type.IsStatic || type.IsAbstract || Symbols.InheritsFrom(type, "System.Exception") || !(type.IsValueType || type.IsSealed || type.IsRecord))
        {
            return;
        }

        var data = type.GetMembers().Where(IsDataMember).ToArray();
        if (data.Length != 1 || !IsImmutable(data[0]))
        {
            return;
        }

        if (type.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && !(type.IsValueType && c.IsImplicitlyDeclared)))
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.ValueObjectShape, type.Locations[0], type.Name, "has a public constructor; use a static Create returning Result<T>"));
        }

        var results = roles.List(type, "qg_result_type_names", ShapeDescriptors.ResultTypeNames);
        var factories = roles.List(type, "qg_value_object_factory_names", ShapeDescriptors.FactoryNames);
        var hasFactory = type.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsStatic && factories.Contains(m.Name)
            && m.ReturnType is INamedTypeSymbol { TypeArguments.Length: 1 } returned && results.Contains(returned.Name)
            && SymbolEqualityComparer.Default.Equals(returned.TypeArguments[0], type));
        if (!hasFactory)
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.ValueObjectShape, type.Locations[0], type.Name, "has no static Create returning Result<T>"));
        }
    }

    private static bool IsDataMember(ISymbol member) => member switch
    {
        IPropertySymbol { IsStatic: false, IsIndexer: false, Name: not "EqualityContract" } => true,
        IFieldSymbol { IsStatic: false, AssociatedSymbol: null, IsConst: false } => true,
        _ => false,
    };

    private static bool IsImmutable(ISymbol member) => member switch
    {
        IPropertySymbol property => property.SetMethod is null || property.SetMethod.IsInitOnly,
        IFieldSymbol field => field.IsReadOnly,
        _ => false,
    };

    private static void Attributes(SymbolAnalysisContext context, INamedTypeSymbol type, ShapeRoles roles)
    {
        var banned = roles.List(type, "qg_persistence_attributes", ShapeDescriptors.PersistenceAttributes);
        var owners = new[] { (ISymbol)type }.Concat(type.GetMembers().Where(m => m is IPropertySymbol or IFieldSymbol && !m.IsImplicitlyDeclared));
        foreach (var owner in owners)
        {
            foreach (var attribute in owner.GetAttributes())
            {
                var name = attribute.AttributeClass?.Name ?? string.Empty;
                name = name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - "Attribute".Length) : name;
                if (banned.Contains(name))
                {
                    var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? owner.Locations[0];
                    context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.PersistenceAttributeInModel, location, name, owner.Name));
                }
            }
        }
    }
}

internal static class ContractShape
{
    public static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        if (type.IsAbstract && !type.IsRecord || Symbols.InheritsFrom(type, "System.Exception") || Symbols.InheritsFrom(type, "System.Attribute"))
        {
            return;
        }

        if (!type.IsRecord)
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.ContractShape, type.Locations[0], type.Name, "is not a record; use a record with required init properties"));
            return;
        }

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>().Where(IsChecked))
        {
            var detail = property.SetMethod switch
            {
                { IsInitOnly: false } => $"property '{property.Name}' has a setter; use init",
                { IsInitOnly: true } when !property.IsRequired && !HasInitializer(property) && !IsNullable(property) => $"property '{property.Name}' is init but not required",
                _ => null,
            };
            if (detail is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.ContractShape, property.Locations[0], type.Name, detail));
            }
        }
    }

    private static bool IsChecked(IPropertySymbol property) =>
        !property.IsStatic && !property.IsIndexer && !property.IsOverride && !property.IsImplicitlyDeclared
        && property.DeclaredAccessibility == Accessibility.Public
        && property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax;

    private static bool HasInitializer(IPropertySymbol property) =>
        property.DeclaringSyntaxReferences.First().GetSyntax() is PropertyDeclarationSyntax { Initializer: not null };

    private static bool IsNullable(IPropertySymbol property) => property.NullableAnnotation == NullableAnnotation.Annotated;
}

internal static class TechnologyNames
{
    public static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol type, ShapeRoles roles)
    {
        var words = roles.List(type, "qg_technology_words", ShapeDescriptors.TechnologyWords);
        Check(context, type.Name, type.Locations[0], words);
        foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared && !m.IsOverride && m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IFieldSymbol))
        {
            if (!Symbols.ImplementsInterface(member))
            {
                Check(context, member.Name, member.Locations[0], words);
            }
        }
    }

    private static bool ContainsToken(string name, string word)
    {
        for (var i = name.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = name.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + word.Length;
            var starts = i == 0 || (char.IsUpper(name[i]) && (!char.IsLetter(name[i - 1]) || char.IsLower(name[i - 1]) || (i == 1 && name[0] == 'I')));
            if (starts && (end == name.Length || !char.IsLower(name[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static void Check(SymbolAnalysisContext context, string name, Location location, ImmutableHashSet<string> words)
    {
        var tokens = Tokens.Split(name);
        var word = tokens.FirstOrDefault(words.Contains)
            ?? words.FirstOrDefault(w => w.Any(char.IsDigit) && ContainsToken(name, w));
        if (word is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.TechnologyWord, location, name, word));
        }
    }
}
