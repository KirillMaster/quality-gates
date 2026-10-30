using System.Collections.Concurrent;
using System.Globalization;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HardcodeAnalyzer : QgAnalyzer
{
    private const string SchemeUrl = @"^[A-Za-z][A-Za-z0-9+.\-]*://\S+";
    private const string Ipv4 = @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?(/\S*)?$";

    private static readonly ConcurrentDictionary<ImmutableHashSet<string>, string> HostPatterns = new();

    private static readonly ImmutableHashSet<string> ComparisonMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Equals", "StartsWith", "EndsWith", "Contains", "Parse", "TryParse");

    private static readonly ImmutableHashSet<string> FreeKinds = ImmutableHashSet.Create(StringComparer.Ordinal, "SchemaLimit", "Protocol", "Math");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ShapeDescriptors.MagicNumber, ShapeDescriptors.TimeSpanLiteral, ShapeDescriptors.HardcodedUrl);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var config = new CompilationConfig(start.Options, start.Compilation);
            var roles = new ShapeRoles(config, start.Compilation);
            if (roles.Skip)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(c => AnalyzeNumber(c, config), SyntaxKind.NumericLiteralExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeString(c, config), SyntaxKind.StringLiteralExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeTimeSpan(c, config), SyntaxKind.InvocationExpression);
        });

    private static void AnalyzeNumber(SyntaxNodeAnalysisContext context, CompilationConfig config)
    {
        var literal = (LiteralExpressionSyntax)context.Node;
        if (IsTrivial(literal) || IsTimeSpanArgument(literal) || IsExemptContext(literal, context.Options, config))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.MagicNumber, literal.GetLocation(), literal.Token.Text));
    }

    private static void AnalyzeString(SyntaxNodeAnalysisContext context, CompilationConfig config)
    {
        var literal = (LiteralExpressionSyntax)context.Node;
        var text = literal.Token.ValueText;
        if (!IsEndpoint(text, config, literal.SyntaxTree) || IsComparisonOrVersion(literal) || IsExemptContext(literal, context.Options, config))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.HardcodedUrl, literal.GetLocation(), text));
    }

    private static void AnalyzeTimeSpan(SyntaxNodeAnalysisContext context, CompilationConfig config)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (!IsTimeSpanFactory(invocation) || !HasLiteralArgument(invocation) || IsExemptContext(invocation, context.Options, config))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.TimeSpanLiteral, invocation.GetLocation(), invocation.ToString()));
    }

    private static bool IsTrivial(LiteralExpressionSyntax literal)
    {
        var value = Convert.ToDouble(literal.Token.Value, CultureInfo.InvariantCulture);
        var index = literal.Parent is ArgumentSyntax { Parent: BracketedArgumentListSyntax };
        return value is 0 or 1 || (index && value == 2);
    }

    private static bool IsComparisonOrVersion(LiteralExpressionSyntax literal) => literal.Parent switch
    {
        BinaryExpressionSyntax binary => binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression),
        ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } } =>
            (call.Expression is MemberAccessExpressionSyntax access && (ComparisonMethods.Contains(access.Name.Identifier.ValueText) || access.Expression.ToString() == "Version")),
        ArgumentSyntax { Parent: ArgumentListSyntax { Parent: ObjectCreationExpressionSyntax creation } } => creation.Type.ToString() is "Version" or "System.Version",
        _ => false,
    };

    private static bool IsTimeSpanArgument(LiteralExpressionSyntax literal) =>
        literal.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } } && IsTimeSpanFactory(invocation)
        || literal.Parent is PrefixUnaryExpressionSyntax { Parent: ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax negated } } } && IsTimeSpanFactory(negated);

    private static bool IsTimeSpanFactory(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax access
        && access.Name.Identifier.ValueText.StartsWith("From", StringComparison.Ordinal)
        && access.Expression.ToString() is "TimeSpan" or "System.TimeSpan";

    private static bool HasLiteralArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.Count > 0
        && invocation.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax or PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax };

    private static bool IsEndpoint(string text, CompilationConfig config, SyntaxTree tree)
    {
        if (text.Length < 4 || text.IndexOf(' ') >= 0)
        {
            return false;
        }

        var suffixes = config.List(tree, "qg_url_host_suffixes", ShapeDescriptors.UrlHostSuffixes);
        var host = HostPatterns.GetOrAdd(suffixes, s => @"^(localhost|[a-z0-9\-]+(\.[a-z0-9\-]+)*\.(" + string.Join("|", s.Select(System.Text.RegularExpressions.Regex.Escape)) + @"))(:\d+)?(/\S*)?$");
        return Patterns.IsMatch(SchemeUrl, text) || (Patterns.IsMatch(Ipv4, text) && ValidOctets(text)) || Patterns.IsMatch(host, text);
    }

    private static bool ValidOctets(string text)
    {
        var address = text.Split(':', '/')[0];
        return address.Split('.').All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet) && octet <= 255);
    }

    private static bool IsExemptContext(SyntaxNode node, AnalyzerOptions options, CompilationConfig config)
    {
        if (config.IsCompositionRoot(node))
        {
            return true;
        }

        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AttributeListSyntax or EnumMemberDeclarationSyntax or ParameterSyntax or PatternSyntax or SwitchLabelSyntax:
                    return true;
                case LocalDeclarationStatementSyntax local when local.IsConst:
                    return true;
                case FieldDeclarationSyntax field when HasFreeInvariant(field.AttributeLists, field.Declaration.Variables[0].Identifier.ValueText, options, node.SyntaxTree):
                    return true;
                case PropertyDeclarationSyntax property when HasFreeInvariant(property.AttributeLists, property.Identifier.ValueText, options, node.SyntaxTree):
                    return true;
            }
        }

        return false;
    }

    private static bool HasFreeInvariant(SyntaxList<AttributeListSyntax> lists, string memberName, AnalyzerOptions options, SyntaxTree tree)
    {
        var invariant = lists.SelectMany(l => l.Attributes).FirstOrDefault(a => AttributeName(a) == "Invariant");
        if (invariant is null)
        {
            return false;
        }

        var kind = invariant.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString() ?? string.Empty;
        kind = kind.Substring(kind.LastIndexOf('.') + 1);
        var tunable = Patterns.Option(options, tree, "qg_tunable_name_regex", ShapeDescriptors.TunableNamePattern);
        return FreeKinds.Contains(kind) || !Patterns.IsMatch(tunable, memberName);
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name is QualifiedNameSyntax qualified ? qualified.Right.Identifier.ValueText : attribute.Name.ToString();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - "Attribute".Length) : name;
    }
}
