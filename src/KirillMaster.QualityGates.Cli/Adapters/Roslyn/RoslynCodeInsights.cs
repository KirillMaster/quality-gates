using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KirillMaster.QualityGates.Cli.Adapters.Roslyn;

/// <summary>Syntax-only (no compilation) facts for the process gates. Cognitive complexity follows the SonarSource specification except recursion, which needs semantics.</summary>
public sealed class RoslynCodeInsights : ICodeInsights
{
    private static readonly string[] MapMethods = ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"];

    public IReadOnlyDictionary<string, int> CognitiveComplexity(string source)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in Parse(source).DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
        {
            var body = member switch
            {
                BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                _ => null
            };
            if (body is not null)
            {
                result[KeyOf(member)] = Walk(body, 0);
            }
        }

        return result;
    }

    public IReadOnlyList<string> PublicTypes(string source) =>
        [.. Parse(source).DescendantNodes(n => n is not BaseTypeDeclarationSyntax and not MethodDeclarationSyntax)
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Modifiers.Any(SyntaxKind.PublicKeyword))
            .Select(t => t.Identifier.ValueText)];

    public IReadOnlySet<string> Identifiers(string source) =>
        Parse(source).DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<EndpointFlow> Flows(string source)
    {
        var flows = new List<EndpointFlow>();
        var root = Parse(source);
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText.EndsWith("Controller", StringComparison.Ordinal)))
        {
            var known = KnownTypes(type);
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword)))
            {
                var calls = CallsOf((SyntaxNode?)method.Body ?? method.ExpressionBody, known);
                flows.Add(new EndpointFlow($"{type.Identifier.ValueText}.{method.Identifier.ValueText}", calls));
            }
        }

        foreach (var map in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => i.Expression is MemberAccessExpressionSyntax m && MapMethods.Contains(m.Name.Identifier.ValueText)))
        {
            var verb = ((MemberAccessExpressionSyntax)map.Expression).Name.Identifier.ValueText[3..].ToUpperInvariant();
            var route = map.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            var handler = map.ArgumentList.Arguments.Skip(1).Select(a => a.Expression).OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault();
            if (route is null || handler is null)
            {
                continue;
            }

            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in handler switch { ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters, SimpleLambdaExpressionSyntax s => [s.Parameter], _ => default })
            {
                if (parameter.Type is { } type)
                {
                    known[parameter.Identifier.ValueText] = type.ToString();
                }
            }

            flows.Add(new EndpointFlow($"{verb} {route.Token.ValueText}", CallsOf(handler.Body, known)));
        }

        return flows;
    }

    private static SyntaxNode Parse(string source) => CSharpSyntaxTree.ParseText(source).GetRoot();

    private static Dictionary<string, string> KnownTypes(ClassDeclarationSyntax type)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var constructor in type.Members.OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var parameter in constructor.ParameterList.Parameters.Where(p => p.Type is not null))
            {
                known[parameter.Identifier.ValueText] = parameter.Type!.ToString();
            }
        }

        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                known[variable.Identifier.ValueText] = field.Declaration.Type.ToString();
            }
        }

        foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            known[property.Identifier.ValueText] = property.Type.ToString();
        }

        foreach (var parameter in type.ParameterList?.Parameters ?? default)
        {
            if (parameter.Type is not null)
            {
                known[parameter.Identifier.ValueText] = parameter.Type.ToString();
            }
        }

        return known;
    }

    private static List<string> CallsOf(SyntaxNode? body, IReadOnlyDictionary<string, string> known)
    {
        var calls = new List<string>();
        foreach (var call in body?.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>() ?? [])
        {
            if (call.Expression is MemberAccessExpressionSyntax { Expression: var receiver, Name: var name } && receiver is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
            {
                var key = receiver is IdentifierNameSyntax id ? id.Identifier.ValueText : ((MemberAccessExpressionSyntax)receiver).Name.Identifier.ValueText;
                if (known.TryGetValue(key, out var type))
                {
                    calls.Add($"{type}.{name.Identifier.ValueText}");
                }
            }
        }

        return calls;
    }

    private static string KeyOf(SyntaxNode member)
    {
        var owner = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "?";
        return member switch
        {
            MethodDeclarationSyntax m => $"{owner}.{m.Identifier.ValueText}({Parameters(m.ParameterList)})",
            ConstructorDeclarationSyntax c => $"{owner}.{c.Identifier.ValueText}({Parameters(c.ParameterList)})",
            DestructorDeclarationSyntax => $"{owner}.~{owner}()",
            OperatorDeclarationSyntax o => $"{owner}.operator {o.OperatorToken.ValueText}({Parameters(o.ParameterList)})",
            ConversionOperatorDeclarationSyntax o => $"{owner}.conversion {o.Type}({Parameters(o.ParameterList)})",
            AccessorDeclarationSyntax a => $"{owner}.{(a.Parent?.Parent as BasePropertyDeclarationSyntax is { } p ? PropertyName(p) : "?")}.{a.Keyword.ValueText}",
            _ => owner
        };
    }

    private static string PropertyName(BasePropertyDeclarationSyntax property) => property switch
    {
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax e => e.Identifier.ValueText,
        _ => "?"
    };

    private static string Parameters(ParameterListSyntax list) => string.Join(',', list.Parameters.Select(p => p.Type?.ToString() ?? "?"));

    private static int Walk(SyntaxNode node, int nesting)
    {
        switch (node)
        {
            case IfStatementSyntax ifStatement:
                return (ifStatement.Parent is ElseClauseSyntax ? 1 : 1 + nesting)
                    + Walk(ifStatement.Condition, nesting)
                    + Walk(ifStatement.Statement, nesting + 1)
                    + (ifStatement.Else is { } @else ? ElseCost(@else, nesting) : 0);
            case SwitchStatementSyntax or SwitchExpressionSyntax:
                return 1 + nesting + Children(node, nesting + 1);
            case ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax:
                return 1 + nesting + Children(node, nesting + 1);
            case ConditionalExpressionSyntax:
                return 1 + nesting + Children(node, nesting + 1);
            case GotoStatementSyntax:
                return 1;
            case LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax:
                return Children(node, nesting + 1);
            case BinaryExpressionSyntax binary when IsLogical(binary):
                return LogicalCost(binary, nesting);
            default:
                return Children(node, nesting);
        }
    }

    private static int ElseCost(ElseClauseSyntax @else, int nesting) =>
        @else.Statement is IfStatementSyntax elseIf ? Walk(elseIf, nesting) : 1 + Walk(@else.Statement, nesting + 1);

    private static int Children(SyntaxNode node, int nesting) => node.ChildNodes().Sum(c => Walk(c, nesting));

    private static bool IsLogical(BinaryExpressionSyntax binary) => binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression);

    private static int LogicalCost(BinaryExpressionSyntax root, int nesting)
    {
        var operators = new List<SyntaxKind>();
        var leaves = new List<SyntaxNode>();
        Flatten(root, operators, leaves);
        var sequences = operators.Where((kind, i) => i == 0 || kind != operators[i - 1]).Count();
        return sequences + leaves.Sum(l => Walk(l, nesting));
    }

    private static void Flatten(SyntaxNode node, List<SyntaxKind> operators, List<SyntaxNode> leaves)
    {
        if (node is BinaryExpressionSyntax binary && IsLogical(binary))
        {
            Flatten(binary.Left, operators, leaves);
            operators.Add(binary.Kind());
            Flatten(binary.Right, operators, leaves);
        }
        else
        {
            leaves.Add(node);
        }
    }
}
