using System.Collections.Concurrent;
using System.Collections.Generic;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NamingAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.BannedNameSuffix, Descriptors.AbbreviationToken);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var state = new State(new CompilationConfig(start.Options, start.Compilation));
            start.RegisterSymbolAction(state.AnalyzeType, SymbolKind.NamedType);
            start.RegisterSymbolAction(state.AnalyzeMember, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field);
            start.RegisterSyntaxNodeAction(
                state.AnalyzeLocal,
                SyntaxKind.VariableDeclarator, SyntaxKind.ForEachStatement, SyntaxKind.SingleVariableDesignation,
                SyntaxKind.Parameter, SyntaxKind.CatchDeclaration);
        });

    private sealed class State
    {
        private readonly CompilationConfig _config;
        private readonly ConcurrentDictionary<INamedTypeSymbol, ImmutableHashSet<ISymbol>> _implemented = new(SymbolEqualityComparer.Default);

        public State(CompilationConfig config)
        {
            _config = config;
        }

        public void AnalyzeType(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.IsImplicitlyDeclared || type.Locations.Length == 0 || !type.Locations[0].IsInSource)
            {
                return;
            }

            var tree = type.Locations[0].SourceTree!;
            var tokens = Tokens.Split(type.Name);
            if (tokens.Length > 0 && _config.List(tree, "qg_banned_name_suffixes", Descriptors.BannedNameSuffixes).Contains(tokens[tokens.Length - 1]))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.BannedNameSuffix, type.Locations[0], type.Name, tokens[tokens.Length - 1]));
            }

            ReportAbbreviation(context.ReportDiagnostic, type.Name, type.Locations[0], tree);
        }

        public void AnalyzeMember(SymbolAnalysisContext context)
        {
            var symbol = context.Symbol;
            if (symbol.IsImplicitlyDeclared || symbol.IsOverride || symbol.Locations.Length == 0 || !symbol.Locations[0].IsInSource
                || symbol is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.Constructor) })
            {
                return;
            }

            var tree = symbol.Locations[0].SourceTree!;
            var names = new List<(string Name, Location Location)>();
            if (symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor })
            {
                names.Add((symbol.Name, symbol.Locations[0]));
            }

            if (symbol is IMethodSymbol method)
            {
                names.AddRange(method.Parameters.Where(p => !p.IsImplicitlyDeclared && p.Locations.Length > 0).Select(p => (p.Name, p.Locations[0])));
            }

            var abbreviations = _config.List(tree, "qg_abbreviation_tokens", Descriptors.AbbreviationTokens);
            var hits = names.Where(n => FindToken(n.Name, abbreviations) is not null).ToArray();
            if (hits.Length == 0 || ImplementsInterface(symbol))
            {
                return;
            }

            foreach (var (name, location) in hits)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AbbreviationToken, location, name, FindToken(name, abbreviations)));
            }
        }

        public void AnalyzeLocal(SyntaxNodeAnalysisContext context)
        {
            var (name, location) = context.Node switch
            {
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: not (FieldDeclarationSyntax or EventFieldDeclarationSyntax) } } v
                    => (v.Identifier.ValueText, v.Identifier.GetLocation()),
                ForEachStatementSyntax f => (f.Identifier.ValueText, f.Identifier.GetLocation()),
                SingleVariableDesignationSyntax d => (d.Identifier.ValueText, d.Identifier.GetLocation()),
                ParameterSyntax { Parent: ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax } p => (p.Identifier.ValueText, p.Identifier.GetLocation()),
                ParameterSyntax { Parent: ParameterListSyntax { Parent: ParenthesizedLambdaExpressionSyntax or LocalFunctionStatementSyntax } } p => (p.Identifier.ValueText, p.Identifier.GetLocation()),
                CatchDeclarationSyntax c => (c.Identifier.ValueText, c.Identifier.GetLocation()),
                _ => (string.Empty, Location.None),
            };
            if (name.Length > 0)
            {
                ReportAbbreviation(context.ReportDiagnostic, name, location, context.Node.SyntaxTree);
            }
        }

        private static string? FindToken(string name, ImmutableHashSet<string> abbreviations) =>
            Tokens.Split(name).FirstOrDefault(abbreviations.Contains);

        private void ReportAbbreviation(Action<Diagnostic> report, string name, Location location, SyntaxTree tree)
        {
            var token = FindToken(name, _config.List(tree, "qg_abbreviation_tokens", Descriptors.AbbreviationTokens));
            if (token is not null)
            {
                report(Diagnostic.Create(Descriptors.AbbreviationToken, location, name, token));
            }
        }

        private bool ImplementsInterface(ISymbol member)
        {
            if (member.ContainingType is not { } type)
            {
                return false;
            }

            var implemented = _implemented.GetOrAdd(type, t => t.AllInterfaces
                .SelectMany(i => i.GetMembers())
                .Select(i => t.FindImplementationForInterfaceMember(i))
                .OfType<ISymbol>()
                .ToImmutableHashSet<ISymbol>(SymbolEqualityComparer.Default));
            return implemented.Contains(member);
        }
    }
}
