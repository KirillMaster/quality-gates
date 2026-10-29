namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NamingAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.BannedNameSuffix, Descriptors.AbbreviationToken);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        context.RegisterSymbolAction(AnalyzeMember, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field);
        context.RegisterSyntaxNodeAction(AnalyzeLocal, SyntaxKind.VariableDeclarator, SyntaxKind.ForEachStatement);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.IsImplicitlyDeclared || type.Locations.Length == 0 || !type.Locations[0].IsInSource)
        {
            return;
        }

        var tree = type.Locations[0].SourceTree!;
        var tokens = Tokens.Split(type.Name);
        var banned = Thresholds.GetList(context.Options, tree, "qg_banned_name_suffixes", Descriptors.BannedNameSuffixes);
        if (tokens.Length > 0 && banned.Contains(tokens[tokens.Length - 1]))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BannedNameSuffix, type.Locations[0], type.Name, tokens[tokens.Length - 1]));
        }

        ReportAbbreviation(context, type.Name, type.Locations[0], tree);
    }

    private static void AnalyzeMember(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared || symbol.IsOverride || symbol.Locations.Length == 0 || !symbol.Locations[0].IsInSource
            || symbol is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.Constructor) }
            || Symbols.ImplementsInterface(symbol))
        {
            return;
        }

        var tree = symbol.Locations[0].SourceTree!;
        if (symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor })
        {
            ReportAbbreviation(context, symbol.Name, symbol.Locations[0], tree);
        }

        if (symbol is IMethodSymbol method)
        {
            foreach (var parameter in method.Parameters.Where(p => !p.IsImplicitlyDeclared && p.Locations.Length > 0))
            {
                ReportAbbreviation(context, parameter.Name, parameter.Locations[0], tree);
            }
        }
    }

    private static void AnalyzeLocal(SyntaxNodeAnalysisContext context)
    {
        var (name, location) = context.Node switch
        {
            VariableDeclaratorSyntax { Parent.Parent: LocalDeclarationStatementSyntax } v => (v.Identifier.ValueText, v.Identifier.GetLocation()),
            ForEachStatementSyntax f => (f.Identifier.ValueText, f.Identifier.GetLocation()),
            _ => (string.Empty, Location.None),
        };
        if (name.Length > 0)
        {
            var abbreviations = Thresholds.GetList(context.Options, context.Node.SyntaxTree, "qg_abbreviation_tokens", Descriptors.AbbreviationTokens);
            Report(context.ReportDiagnostic, name, location, abbreviations);
        }
    }

    private static void ReportAbbreviation(SymbolAnalysisContext context, string name, Location location, SyntaxTree tree) =>
        Report(context.ReportDiagnostic, name, location, Thresholds.GetList(context.Options, tree, "qg_abbreviation_tokens", Descriptors.AbbreviationTokens));

    private static void Report(Action<Diagnostic> report, string name, Location location, ImmutableHashSet<string> abbreviations)
    {
        var token = Tokens.Split(name).FirstOrDefault(abbreviations.Contains);
        if (token is not null)
        {
            report(Diagnostic.Create(Descriptors.AbbreviationToken, location, name, token));
        }
    }
}
