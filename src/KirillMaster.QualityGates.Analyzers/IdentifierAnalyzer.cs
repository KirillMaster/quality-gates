using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdentifierAnalyzer : QgAnalyzer
{
    private static readonly Regex DigitTail = new(@"[A-Za-z]\d+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ReadabilityDescriptors.UnknownWord, ReadabilityDescriptors.BoolWithoutPrefix, ReadabilityDescriptors.DigitSuffix,
        ReadabilityDescriptors.SimilarNames, ReadabilityDescriptors.NameLiesAboutType);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var state = new State(new CompilationConfig(start.Options, start.Compilation));
            start.RegisterSymbolAction(state.AnalyzeSymbol, SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field);
            start.RegisterSyntaxNodeAction(
                state.AnalyzeLocal,
                SyntaxKind.VariableDeclarator, SyntaxKind.ForEachStatement, SyntaxKind.SingleVariableDesignation, SyntaxKind.Parameter);
            start.RegisterSyntaxNodeAction(State.AnalyzeScope, SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration);
        });

    private sealed class State
    {
        private readonly CompilationConfig _config;
        private readonly ConcurrentDictionary<IMethodSymbol, bool> _tests = new(SymbolEqualityComparer.Default);
        private readonly ImmutableHashSet<string>? _glossary;
        private readonly ConcurrentDictionary<INamedTypeSymbol, ImmutableHashSet<ISymbol>> _implemented = new(SymbolEqualityComparer.Default);

        public State(CompilationConfig config)
        {
            _config = config;
            _glossary = LoadGlossary(config.Options);
        }

        public void AnalyzeSymbol(SymbolAnalysisContext context)
        {
            var symbol = context.Symbol;
            if (!Identifiers.IsDeclaredHere(symbol) || symbol.IsOverride || symbol is IMethodSymbol { MethodKind: not MethodKind.Ordinary }
                || symbol is IPropertySymbol { IsIndexer: true } || ImplementsInterface(symbol))
            {
                return;
            }

            var tree = symbol.Locations[0].SourceTree!;
            var type = symbol switch
            {
                IFieldSymbol f => f.Type,
                IPropertySymbol p => p.Type,
                IMethodSymbol m => m.ReturnType,
                _ => null,
            };
            var isTest = symbol is IMethodSymbol method && _tests.GetOrAdd(method, Symbols.IsTestMethod) || IsContract(symbol);
            Check(context.ReportDiagnostic, symbol.Name, symbol.Locations[0], tree, type, isMember: true, isTest);
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } ordinary)
            {
                foreach (var parameter in ordinary.Parameters.Where(Identifiers.IsDeclaredHere))
                {
                    Check(context.ReportDiagnostic, parameter.Name, parameter.Locations[0], tree, parameter.Type, isMember: false, isTest);
                }
            }
        }

        public void AnalyzeLocal(SyntaxNodeAnalysisContext context)
        {
            if (Identifiers.Local(context.Node) is not var (name, location))
            {
                return;
            }

            var type = context.SemanticModel.GetDeclaredSymbol(context.Node, context.CancellationToken) switch
            {
                ILocalSymbol l => l.Type,
                IParameterSymbol p => p.Type,
                _ => null,
            };
            Check(context.ReportDiagnostic, name, location, context.Node.SyntaxTree, type, isMember: false, isTest: false);
        }

        public static void AnalyzeScope(SyntaxNodeAnalysisContext context)
        {
            var declaration = (BaseMethodDeclarationSyntax)context.Node;
            var names = declaration.ParameterList.Parameters.Select(p => (p.Identifier.ValueText, p.Identifier.GetLocation()))
                .Concat(declaration.DescendantNodes().Select(Identifiers.Local).Where(l => l.HasValue).Select(l => (l!.Value.Name, l.Value.Location)))
                .Where(n => n.Item1.Length >= 6)
                .Take(60)
                .ToList();
            for (var i = 1; i < names.Count; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (AreSimilar(names[j].Item1, names[i].Item1))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.SimilarNames, names[i].Item2, names[i].Item1, names[j].Item1));
                        break;
                    }
                }
            }
        }

        private void Check(Action<Diagnostic> report, string name, Location location, SyntaxTree tree, ITypeSymbol? type, bool isMember, bool isTest)
        {
            var tokens = Tokens.Split(name);
            CheckUnknownWord(report, name, location, tokens);
            if (!isTest && DigitTail.IsMatch(name)
                && !IsAllowedDigitName(name, _config.List(tree, "qg_digit_suffix_allowed", ReadabilityDescriptors.DigitSuffixAllowed)))
            {
                report(Diagnostic.Create(ReadabilityDescriptors.DigitSuffix, location, name));
            }

            if (type is null || tokens.Length == 0)
            {
                return;
            }

            var unwrapped = Symbols.UnwrapTask(type);
            if (isMember && !isTest && unwrapped.SpecialType == SpecialType.System_Boolean
                && !_config.List(tree, "qg_bool_name_prefixes", ReadabilityDescriptors.BoolPrefixes).Contains(tokens[0]))
            {
                report(Diagnostic.Create(ReadabilityDescriptors.BoolWithoutPrefix, location, name));
            }

            CheckTypeLie(report, name, location, tokens, type);
        }

        private static bool IsAllowedDigitName(string name, ImmutableHashSet<string> allowed)
        {
            var stem = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return allowed.Any(a => name.EndsWith(a, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, a, StringComparison.OrdinalIgnoreCase) || stem.EndsWith(a, StringComparison.Ordinal));
        }

        private static bool IsContract(ISymbol symbol)
        {
            if (symbol is not (IPropertySymbol or IFieldSymbol))
            {
                return false;
            }

            if (symbol.GetAttributes().Any(a => a.AttributeClass?.Name is "ColumnAttribute" or "JsonPropertyNameAttribute" or "JsonPropertyAttribute"
                or "DataMemberAttribute" or "XmlElementAttribute" or "XmlAttributeAttribute" or "KeyAttribute" or "FromQueryAttribute" or "BsonElementAttribute"))
            {
                return true;
            }

            var owner = symbol.ContainingType;
            return owner.DeclaredAccessibility == Accessibility.Public
                && (owner.IsRecord || !owner.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared));
        }

        private void CheckUnknownWord(Action<Diagnostic> report, string name, Location location, string[] tokens)
        {
            if (_glossary is null)
            {
                return;
            }

            var unknown = tokens.FirstOrDefault(t => t.Length > 2 && t.Any(char.IsLower) && !BaseWords.Knows(t, _glossary));
            if (unknown is not null)
            {
                report(Diagnostic.Create(ReadabilityDescriptors.UnknownWord, location, name, unknown));
            }
        }

        private static void CheckTypeLie(Action<Diagnostic> report, string name, Location location, string[] tokens, ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_Object || type.TypeKind is TypeKind.TypeParameter or TypeKind.Dynamic or TypeKind.Error)
            {
                return;
            }

            var last = tokens[tokens.Length - 1];
            var isSystem = type is IArrayTypeSymbol || type.ContainingNamespace?.ToDisplayString().Split('.')[0] == "System";
            var lie = !isSystem ? null : last switch
            {
                "List" when !Symbols.IsCollection(type) => "a collection",
                "Dictionary" when !IsDictionary(type) => "a dictionary",
                _ => null,
            };
            if (lie is null && tokens.Length > 1 && (string.Equals(tokens[0], "Is", StringComparison.OrdinalIgnoreCase) || string.Equals(tokens[0], "Has", StringComparison.OrdinalIgnoreCase) || string.Equals(tokens[0], "Can", StringComparison.OrdinalIgnoreCase)) && Symbols.UnwrapTask(type).SpecialType != SpecialType.System_Boolean
                && !(type is INamedTypeSymbol { Name: "Nullable" } || type.NullableAnnotation == NullableAnnotation.Annotated))
            {
                lie = "a boolean";
            }

            if (lie is not null)
            {
                report(Diagnostic.Create(ReadabilityDescriptors.NameLiesAboutType, location, name, lie, type.Name));
            }
        }

        private static bool IsDictionary(ITypeSymbol type) =>
            new[] { type }.Concat(type.AllInterfaces).Any(t => t.Name is "IDictionary" or "IReadOnlyDictionary" or "ILookup");

        private static bool AreSimilar(string left, string right)
        {
            if (DigitTail.IsMatch(left) && DigitTail.IsMatch(right))
            {
                return false;
            }

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return !string.Equals(left, right, StringComparison.Ordinal);
            }

            var (shorter, longer) = left.Length <= right.Length ? (left.ToLowerInvariant(), right.ToLowerInvariant()) : (right.ToLowerInvariant(), left.ToLowerInvariant());
            if (longer.Length - shorter.Length > 1 || (longer.Length > shorter.Length && longer.EndsWith("s", StringComparison.Ordinal) && longer.StartsWith(shorter, StringComparison.Ordinal)))
            {
                return false;
            }

            var i = 0;
            while (i < shorter.Length && shorter[i] == longer[i])
            {
                i++;
            }

            return string.Equals(shorter.Substring(longer.Length == shorter.Length ? i + 1 : i), longer.Substring(i + 1), StringComparison.Ordinal);
        }

        private static ImmutableHashSet<string>? LoadGlossary(AnalyzerOptions options)
        {
            var words = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            var found = false;
            foreach (var file in options.AdditionalFiles.Where(f => string.Equals(System.IO.Path.GetFileName(f.Path), ReadabilityDescriptors.DictionaryFileName, StringComparison.OrdinalIgnoreCase)))
            {
                found = true;
                var source = file.GetText();
                if (source is null)
                {
                    continue;
                }

                foreach (var line in source.Lines)
                {
                    var text = line.ToString();
                    var hash = text.IndexOf('#');
                    foreach (var word in (hash >= 0 ? text.Substring(0, hash) : text).Split(' ', ',', '\t', ';'))
                    {
                        if (word.Trim().Length > 0)
                        {
                            words.Add(word.Trim());
                        }
                    }
                }
            }

            return found ? words.ToImmutable() : null;
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
