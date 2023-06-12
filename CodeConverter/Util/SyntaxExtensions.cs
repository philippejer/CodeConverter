namespace ICSharpCode.CodeConverter.Util;

internal static class SyntaxExtensions
{
    public static CS.Syntax.ExpressionSyntax SkipIntoParens(this CS.Syntax.ExpressionSyntax expression)
    {
        if (expression == null)
            return null;
        while (expression is CS.Syntax.ParenthesizedExpressionSyntax pes) {
            expression = pes.Expression;
        }
        return expression;
    }
    public static bool AnyInParens(this CS.Syntax.ExpressionSyntax expression, Func<CS.Syntax.ExpressionSyntax, bool> predicate)
    {
        if (expression == null) return false;
        while (true) {
            if (predicate(expression)) return true;
            if (expression is not CS.Syntax.ParenthesizedExpressionSyntax pes) return false;
            expression = pes.Expression;
        }
    }

    public static VBSyntax.ExpressionSyntax SkipIntoParens(this VBSyntax.ExpressionSyntax expression)
    {
        while (expression is VBSyntax.ParenthesizedExpressionSyntax pes) {
            expression = pes.Expression;
        }
        return expression;
    }

    public static SyntaxNode SkipOutOfParens(this SyntaxNode expression)
    {
        while (expression is VBSyntax.ParenthesizedExpressionSyntax pes) {
            expression = pes.Parent;
        }
        return expression;
    }

    public static bool IsParentKind(this SyntaxNode node, CS.SyntaxKind kind)
    {
        return node != null && node.Parent.IsKind(kind);
    }

    public static bool IsParentKind(this SyntaxNode node, VBasic.SyntaxKind kind)
    {
        return node?.Parent.IsKind(kind) == true;
    }

    public static bool IsParentKind(this SyntaxToken node, CS.SyntaxKind kind)
    {
        return node.Parent?.IsKind(kind) == true;
    }

    public static TSymbol GetEnclosingSymbol<TSymbol>(this SemanticModel semanticModel, int position, CancellationToken cancellationToken)
        where TSymbol : ISymbol
    {
        for (var symbol = semanticModel.GetEnclosingSymbol(position, cancellationToken);
             symbol != null;
             symbol = symbol.ContainingSymbol) {
            if (symbol is TSymbol) {
                return (TSymbol)symbol;
            }
        }

        return default(TSymbol);
    }
}