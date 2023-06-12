using SyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ICSharpCode.CodeConverter.CSharp;

internal static class VbSyntaxNodeExtensions
{
    public static CSSyntax.ExpressionSyntax ParenthesizeIfPrecedenceCouldChange(this VBasic.VisualBasicSyntaxNode node, CSSyntax.ExpressionSyntax expression)
    {
        return PrecedenceCouldChange(node) ? SyntaxFactory.ParenthesizedExpression(expression) : expression;
    }

    public static bool PrecedenceCouldChange(this VBasic.VisualBasicSyntaxNode node)
    {
        bool parentIsBinaryExpression = node is VBSyntax.BinaryExpressionSyntax;
        bool parentIsLambda = node.Parent is VBSyntax.LambdaExpressionSyntax;
        bool parentIsNonArgumentExpression = node.Parent is VBSyntax.ExpressionSyntax && node.Parent is not VBSyntax.ArgumentSyntax;
        bool parentIsParenthesis = node.Parent is VBSyntax.ParenthesizedExpressionSyntax;
        bool parentIsMemberAccessExpression = node.Parent is VBSyntax.MemberAccessExpressionSyntax;

        return parentIsMemberAccessExpression || parentIsNonArgumentExpression && !parentIsBinaryExpression && !parentIsLambda && !parentIsParenthesis;
    }

    public static bool AlwaysHasBooleanTypeInCSharp(this VBSyntax.ExpressionSyntax vbNode)
    {
        var parent = vbNode.Parent.SkipOutOfParens();

        return parent is VBSyntax.SingleLineIfStatementSyntax singleLine && singleLine.Condition.SkipIntoParens() == vbNode.SkipIntoParens() ||
               parent is VBSyntax.IfStatementSyntax ifStatement && ifStatement.Condition.SkipIntoParens() == vbNode.SkipIntoParens() ||
               parent is VBSyntax.ElseIfStatementSyntax elseIfStatement && elseIfStatement.Condition.SkipIntoParens() == vbNode.SkipIntoParens() ||
               parent is VBSyntax.TernaryConditionalExpressionSyntax ternary && ternary.Condition.SkipIntoParens() == vbNode.SkipIntoParens();
    }

    public static bool IsPureExpression(this VBSyntax.ExpressionSyntax e, SemanticModel semanticModel)
    {
        e = e.SkipIntoParens();
        if (IsSafelyReusable(e, semanticModel)) return true;
        if (e is VBSyntax.BinaryExpressionSyntax binaryExpression) {
            return IsPureExpression(binaryExpression.Left, semanticModel) && IsPureExpression(binaryExpression.Right, semanticModel);
        }
        if (e is VBSyntax.UnaryExpressionSyntax unaryExpression) {
            return IsPureExpression(unaryExpression.Operand, semanticModel);
        }
        return false;
    }

    public static bool IsSafelyReusable(this VBSyntax.ExpressionSyntax e, SemanticModel semanticModel)
    {
        e = e.SkipIntoParens();
        if (e is VBSyntax.LiteralExpressionSyntax) return true;
        var symbolInfo = VBasic.VisualBasicExtensions.GetSymbolInfo(semanticModel, e);
        if (symbolInfo.Symbol is not { } s) return false;
        return s.IsKind(SymbolKind.Local) || s.IsKind(SymbolKind.Field) || s.IsKind(SymbolKind.Parameter) || s.IsAutoProperty();
    }
}