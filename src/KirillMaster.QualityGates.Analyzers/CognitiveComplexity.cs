namespace KirillMaster.QualityGates.Analyzers;

internal sealed class CognitiveComplexity : CSharpSyntaxWalker
{
    private readonly ISymbol? _owner;
    private readonly SemanticModel _model;
    private int _nesting;
    private bool _recursionCounted;

    private CognitiveComplexity(ISymbol? owner, SemanticModel model)
    {
        _owner = owner?.OriginalDefinition;
        _model = model;
    }

    public int Score { get; private set; }

    public static int Of(SyntaxNode body, ISymbol? owner, SemanticModel model)
    {
        var walker = new CognitiveComplexity(owner, model);
        walker.Visit(body);
        return walker.Score;
    }

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        Score += node.Parent is ElseClauseSyntax ? 1 : 1 + _nesting;
        Visit(node.Condition);
        Nested(() => Visit(node.Statement));
        if (node.Else is not null)
        {
            Visit(node.Else);
        }
    }

    public override void VisitElseClause(ElseClauseSyntax node)
    {
        if (node.Statement is IfStatementSyntax)
        {
            Visit(node.Statement);
            return;
        }

        Score += 1;
        Nested(() => Visit(node.Statement));
    }

    public override void VisitForStatement(ForStatementSyntax node) => Structural(() => base.VisitForStatement(node));

    public override void VisitForEachStatement(ForEachStatementSyntax node) => Structural(() => base.VisitForEachStatement(node));

    public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node) => Structural(() => base.VisitForEachVariableStatement(node));

    public override void VisitWhileStatement(WhileStatementSyntax node) => Structural(() => base.VisitWhileStatement(node));

    public override void VisitDoStatement(DoStatementSyntax node) => Structural(() => base.VisitDoStatement(node));

    public override void VisitSwitchStatement(SwitchStatementSyntax node) => Structural(() => base.VisitSwitchStatement(node));

    public override void VisitSwitchExpression(SwitchExpressionSyntax node) => Structural(() => base.VisitSwitchExpression(node));

    public override void VisitConditionalExpression(ConditionalExpressionSyntax node) => Structural(() => base.VisitConditionalExpression(node));

    public override void VisitCatchClause(CatchClauseSyntax node) => Structural(() => base.VisitCatchClause(node));

    public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => Nested(() => base.VisitSimpleLambdaExpression(node));

    public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => Nested(() => base.VisitParenthesizedLambdaExpression(node));

    public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => Nested(() => base.VisitAnonymousMethodExpression(node));

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => Nested(() => base.VisitLocalFunctionStatement(node));

    public override void VisitGotoStatement(GotoStatementSyntax node)
    {
        if (node.IsKind(SyntaxKind.GotoStatement))
        {
            Score += 1;
        }

        base.VisitGotoStatement(node);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression))
        {
            if (Outer(node) is not BinaryExpressionSyntax parent || !parent.IsKind(node.Kind()))
            {
                Score += 1;
            }
        }

        base.VisitBinaryExpression(node);
    }

    public override void VisitBinaryPattern(BinaryPatternSyntax node)
    {
        if (Outer(node) is not BinaryPatternSyntax parent || !parent.IsKind(node.Kind()))
        {
            Score += 1;
        }

        base.VisitBinaryPattern(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (!_recursionCounted && IsSelfCall(node))
        {
            _recursionCounted = true;
            Score += 1;
        }

        base.VisitInvocationExpression(node);
    }

    private static SyntaxNode? Outer(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax or ParenthesizedPatternSyntax)
        {
            parent = parent.Parent;
        }

        return parent;
    }

    private bool IsSelfCall(InvocationExpressionSyntax node) =>
        _owner is not null
        && SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(node).Symbol?.OriginalDefinition, _owner);

    private void Structural(Action visit)
    {
        Score += 1 + _nesting;
        Nested(visit);
    }

    private void Nested(Action visit)
    {
        _nesting++;
        visit();
        _nesting--;
    }
}
