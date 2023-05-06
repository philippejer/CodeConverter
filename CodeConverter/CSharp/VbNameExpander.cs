using System.Diagnostics;
using ICSharpCode.CodeConverter.Util.FromRoslyn;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.CodeConverter.CSharp;

internal class VbNameExpander : ISyntaxExpander
{
    private static readonly SyntaxToken _dotToken = SyntaxFactory.Token(SyntaxKind.DotToken);
    public static ISyntaxExpander Instance { get; } = new VbNameExpander();

    public bool ShouldExpandWithinNode(SyntaxNode node, SemanticModel semanticModel) =>
        !ShouldExpandNode(node, semanticModel) && !IsRoslynInstanceExpressionBug(node as MemberAccessExpressionSyntax);

    public bool ShouldExpandNode(SyntaxNode node, SemanticModel semanticModel) =>
        ShouldExpandName(node) || ShouldExpandMemberAccess(node, semanticModel) || ShouldExpandBinaryExpression(node);

    private static bool ShouldExpandMemberAccess(SyntaxNode node, SemanticModel semanticModel)
    {
        return node is MemberAccessExpressionSyntax maes && !IsRoslynInstanceExpressionBug(maes) &&
               !IsRoslynElementAccessBug(maes) &&
               ShouldBeQualified(node, semanticModel.GetSymbolInfo(node).Symbol, semanticModel);
    }

    /// <summary>
    /// https://github.com/icsharpcode/CodeConverter/issues/765 Roslyn turns dataReader["foo"] into dataReader.Item
    /// </summary>
    private static bool IsRoslynElementAccessBug(MemberAccessExpressionSyntax maes) => maes.IsKind(SyntaxKind.DictionaryAccessExpression);

    private static bool ShouldExpandName(SyntaxNode node) =>
        node is NameSyntax && NameCanBeExpanded(node);

    private static bool ShouldExpandBinaryExpression(SyntaxNode node) =>
        node is BinaryExpressionSyntax && node.IsKind(SyntaxKind.AndExpression, SyntaxKind.OrExpression);

    public SyntaxNode ExpandNode(SyntaxNode node, SemanticModel semanticModel,
        Workspace workspace)
    {
        if (HandleNonShortCircuitingBooleanOperators(ref node, semanticModel)) {
            return node;
        }

        var symbol = semanticModel.GetSymbolInfo(node).Symbol;
        if (node is SimpleNameSyntax sns && GetDefaultImplicitInstance(sns, symbol, semanticModel) is {} defaultImplicitInstance)
        {
            var nameNoTrivia = sns.WithoutLeadingTrivia();
            var leadingTrivia = sns.GetLeadingTrivia();

            if (defaultImplicitInstance.InheritsFromOrEquals(symbol.ContainingType))
            {
                var meKeyword = SyntaxFactory.Token(leadingTrivia, SyntaxKind.MeKeyword);

                return MemberAccess(SyntaxFactory.MeExpression(meKeyword), nameNoTrivia);
            }

            if (semanticModel.GetOperation(node) is IMemberReferenceOperation { Instance: { Syntax: ExpressionSyntax implicitInstance } })
            {
                return MemberAccess(implicitInstance.WithPrependedLeadingTrivia(leadingTrivia), nameNoTrivia);
            }

            if (IsReducedExtensionInExtendedTypeOrDerivedType(node, symbol, semanticModel))
            {
                var meKeyword = SyntaxFactory.Token(leadingTrivia, SyntaxKind.MeKeyword);

                return MemberAccess(SyntaxFactory.MeExpression(meKeyword), nameNoTrivia);
            }
        }

        if (node is MemberAccessExpressionSyntax && IsTypePromotion(node, symbol, semanticModel) &&
            semanticModel.GetOperation(node) is IMemberReferenceOperation { Instance: { Syntax: ExpressionSyntax promotedInstance }, Member: {} member }) {
            return MemberAccess(promotedInstance, SyntaxFactory.IdentifierName(member.Name));
        }

        var result = IsOriginalSymbolGenericMethod(semanticModel, node) ? node : Simplifier.Expand(node, semanticModel, workspace);

        if (node is IdentifierNameSyntax && symbol != null &&  symbol.IsModuleMember()) {
            result = result.WithAdditionalAnnotations(AnnotationConstants.ExtraStaticUsing(symbol.ContainingSymbol));
        }

        return result;
    }

    private static bool IsReducedExtensionInExtendedTypeOrDerivedType(SyntaxNode node, ISymbol symbol, SemanticModel semanticModel)
    {
        return symbol.IsReducedExtension() && IsReceiverTypeOrDerivedTypeEnclosingType(node, (IMethodSymbol)symbol, semanticModel);
    }

    private static bool IsReceiverTypeOrDerivedTypeEnclosingType(SyntaxNode node, IMethodSymbol symbol, SemanticModel semanticModel)
    {
        var classType = (ITypeSymbol)node.GetEnclosingDeclaredTypeSymbol(semanticModel);

        return classType.InheritsFromOrEquals(symbol.ReceiverType);
    }

    /// <summary>
    /// Aim to qualify each name once at the highest level we can get the correct qualification.
    /// i.e. qualify "b.c" to "a.b.c", don't recurse in and try to qualify b or c alone.
    /// We recurse in until we find a static symbol, or find something that Roslyn's expand doesn't deal with sufficiently
    /// This leaves the possibility of not qualifying some instance references which didn't contain
    /// </summary>
    private static bool ShouldBeQualified(SyntaxNode node,
        ISymbol symbol, SemanticModel semanticModel)
    {
        return symbol?.IsStatic == true || CouldBeMyBaseBug(node, symbol, semanticModel) || IsTypePromotion(node, symbol, semanticModel);
    }

    /// <summary>Need to workaround roslyn bug that accidentally uses MyBase instead of Me, or implicit instances like Forms</summary>
    /// See https://github.com/dotnet/roslyn/blob/97123b393c3a5a91cc798b329db0d7fc38634784/src/Workspaces/VisualBasic/Portable/Simplification/VisualBasicSimplificationService.Expander.vb#L657</returns>
    private static bool CouldBeMyBaseBug(SyntaxNode node, ISymbol symbol, SemanticModel semanticModel) =>
        node is SimpleNameSyntax sns && IsLeftMostQualifier(sns) && GetDefaultImplicitInstance(sns, symbol, semanticModel) is {};

    private static bool IsLeftMostQualifier(SimpleNameSyntax node) =>
        node?.Parent switch {
            MemberAccessExpressionSyntax maes => maes.Expression == node,
            ConditionalAccessExpressionSyntax caes => caes.Expression == node,
            _ => true
        };

    private static bool NameCanBeExpanded(SyntaxNode node)
    {
        // Workaround roslyn bug where it will try to expand something even when the parent node cannot contain the type of the expanded node
        if (node.Parent is NameColonEqualsSyntax || node.Parent is NamedFieldInitializerSyntax) return false;
        // Workaround roslyn bug where it duplicates the inferred name
        if (node.Parent is InferredFieldInitializerSyntax) return false;
        return true;
    }

    private static INamedTypeSymbol GetDefaultImplicitInstance(SimpleNameSyntax node, ISymbol symbol, SemanticModel semanticModel, CancellationToken cancellationToken = default) =>
        IsQualifiableInstanceReference(symbol) && IsLeftMostQualifier(node) ? semanticModel.GetEnclosingSymbol<INamedTypeSymbol>(node.SpanStart, cancellationToken) : null;

    private static bool IsTypePromotion(SyntaxNode node, ISymbol symbol, SemanticModel semanticModel)
    {
        if (IsQualifiableInstanceReference(symbol) && node is MemberAccessExpressionSyntax maes && maes.Expression != null) {
            var qualifyingType = semanticModel.GetTypeInfo(maes.Expression).Type;
            return qualifyingType == null || !qualifyingType.ContainsMember(symbol);
        }

        return false;
    }

    /// <summary>
    /// Roslyn bug - accidentally expands "New" into an identifier causing compile error
    /// </summary>
    public static bool IsRoslynInstanceExpressionBug(MemberAccessExpressionSyntax node) =>
        node?.Expression is InstanceExpressionSyntax;

    /// <summary>
    /// Roslyn bug - accidentally expands anonymous types to just "Global."
    /// Since the C# reducer also doesn't seem to reduce generic extension methods, it's best to avoid those too, so let's just avoid all generic methods
    /// </summary>
    private static bool IsOriginalSymbolGenericMethod(SemanticModel semanticModel, SyntaxNode node) =>
        semanticModel.GetSymbolInfo(node).Symbol.IsGenericMethod();

    private static bool IsQualifiableInstanceReference(ISymbol symbol) =>
        symbol?.IsStatic == false && (symbol.IsKind(SymbolKind.Method) || symbol.IsKind(SymbolKind.Field) ||
                                      symbol.IsKind(SymbolKind.Property));

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax expressionSyntax, SimpleNameSyntax sns) =>
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, expressionSyntax, _dotToken, sns);

    public bool HandleNonShortCircuitingBooleanOperators(ref SyntaxNode node, SemanticModel semanticModel)
    {
        if (node is not BinaryExpressionSyntax bin || !bin.Right.IsPureExpression(semanticModel)) {
            return false;
        }

        Debug.Assert(node.IsKind(SyntaxKind.AndExpression, SyntaxKind.OrExpression));

        var leftTypeInfo = semanticModel.GetTypeInfo(bin.Left).ConvertedType;
        var rightTypeInfo = semanticModel.GetTypeInfo(bin.Right).ConvertedType;
        if (!leftTypeInfo.IsBooleanType() || !rightTypeInfo.IsBooleanType()) {
            return true;
        }

        for (var currentNode = (ExpressionSyntax)node; currentNode != null; currentNode = currentNode.Parent as ExpressionSyntax) {
            if (currentNode.AlwaysHasBooleanTypeInCSharp()) {
                if (node.IsKind(SyntaxKind.AndExpression)) {
                    node = SyntaxFactory.BinaryExpression(SyntaxKind.AndAlsoExpression, bin.Left, SyntaxFactory.Token(SyntaxKind.AndAlsoKeyword), bin.Right);
                    return true;
                }

                if (node.IsKind(SyntaxKind.OrExpression)) {
                    node = SyntaxFactory.BinaryExpression(SyntaxKind.OrElseExpression, bin.Left, SyntaxFactory.Token(SyntaxKind.OrElseKeyword), bin.Right);
                    return true;
                }
            }
        }

        return true;
    }
}