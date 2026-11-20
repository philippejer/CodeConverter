using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Razor.Parser.SyntaxTree;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;

/// <summary>
/// Each view fragment is converted on its own, but its meaning depends on the locals declared by enclosing blocks
/// (e.g. the loop variable of a surrounding For Each, or a Dim in an earlier @Code block).
/// Like ASP.NET's own view compilation, this lays out all the view's code in source order as one method of a class
/// inheriting WebViewPage(Of TModel), then asks Roslyn which locals are in scope at each fragment, and with which types.
/// </summary>
public class ViewScope
{
    private const string MarkupCall = "__M()";
    private readonly SemanticModel _semanticModel;
    private readonly Dictionary<Block, int> _positions;

    private ViewScope(SemanticModel semanticModel, Dictionary<Block, int> positions)
    {
        _semanticModel = semanticModel;
        _positions = positions;
    }

    /// <summary>The class ASP.NET MVC compiles a view into inherits this (Views/web.config pageBaseType, with @ModelType as the type argument).</summary>
    public static string PageBaseType(string modelVbType) =>
        modelVbType == null ? "System.Web.Mvc.WebViewPage" : "System.Web.Mvc.WebViewPage(Of " + modelVbType + ")";

    public static ViewScope Build(Block document, string modelVbType, IEnumerable<MetadataReference> references, VisualBasicCompilationOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MustInherit Class __View").AppendLine("    Inherits " + PageBaseType(modelVbType))
          .AppendLine("    Sub __M()").AppendLine("    End Sub")
          .AppendLine("    Sub __W(o As Object)").AppendLine("    End Sub");
        var positions = new Dictionary<Block, int>(ReferenceEqualityComparer.Instance);
        AppendPageMembers(document, sb, positions);
        sb.AppendLine("    Sub __Execute()");
        AppendCode(document, sb, positions); // skips @Functions and @Helper blocks, they're members
        EnsureNewLine(sb);
        sb.AppendLine("    End Sub").AppendLine("End Class");

        var tree = VisualBasicSyntaxTree.ParseText(sb.ToString());
        var compilation = VisualBasicCompilation.Create("__ViewScope", new[] { tree }, references, options);
        if (Environment.GetEnvironmentVariable("VBHTML_DEBUG") != null) Console.WriteLine("[SCOPE]" + Environment.NewLine + sb);
        return new ViewScope(compilation.GetSemanticModel(tree), positions);
    }

    /// <summary>
    /// Locals (and parameters, e.g. of the enclosing @Helper) visible where the fragment starts, as (name, VB type) declarations.
    /// Excludes anything VB can't declare by name.
    /// </summary>
    public IEnumerable<(string Name, string VbType)> LocalsInScope(Block fragment)
    {
        if (fragment == null || !_positions.TryGetValue(fragment, out var position)) return Enumerable.Empty<(string, string)>();
        return _semanticModel.LookupSymbols(position)
            .Select(s => (Symbol: s, Type: (s as ILocalSymbol)?.Type ?? (s as IParameterSymbol)?.Type))
            .Where(l => l.Type != null)
            .Where(l => l.Symbol.Locations.All(loc => loc.SourceSpan.Start < position)) // VB scopes a Dim to its whole block, but it's only usable after it
            .Where(l => !l.Type.IsAnonymousType && l.Type.TypeKind != TypeKind.Error)
            .Select(l => (l.Symbol.Name, l.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
    }

    // ---- @Functions and @Helper: members of the page class rather than code of its Execute method ----

    /// <summary>A helper is a method returning the markup it writes.</summary>
    private const string HelperReturnType = "System.Web.WebPages.HelperResult";

    public static IEnumerable<Block> MemberBlocks(Block block) =>
        block.Children.OfType<Block>().SelectMany(b => b.Type == BlockType.Functions || b.Type == BlockType.Helper ? new[] { b } : MemberBlocks(b));

    /// <summary>The member declarations between Functions and End Functions.</summary>
    public static string FunctionsCode(Block functions) =>
        string.Concat(functions.Children.OfType<Span>().Where(s => s.Kind == SpanKind.Code).Select(s => s.Content));

    /// <summary>@Helper parses as its signature (e.g. "Name(x As T)") followed by a statement block for its body, ending with End Helper.</summary>
    public static (string Signature, Block Body) HelperParts(Block helper) =>
        (string.Concat(helper.Children.OfType<Span>().Where(s => s.Kind == SpanKind.Code).Select(s => s.Content)).Trim(),
         helper.Children.OfType<Block>().FirstOrDefault());

    public static string HelperName(string signature) =>
        (signature.IndexOf('(') is var paren && paren >= 0 ? signature.Substring(0, paren) : signature).Trim();

    /// <summary>VB declarations of the view's own members, so code calling them resolves: @Functions code as is, @Helper as a stub.</summary>
    public static string PageMembersVb(Block document) =>
        string.Concat(MemberBlocks(document).Where(m => m.Type == BlockType.Functions).Select(m => FunctionsCode(m) + Environment.NewLine))
        + HelperStubsVb(document);

    /// <summary>Just the @Helper stubs: a helper's caller only needs its signature.</summary>
    public static string HelperStubsVb(Block document) => string.Concat(MemberBlocks(document).Where(m => m.Type == BlockType.Helper).Select(m =>
        "Function " + HelperParts(m).Signature + " As " + HelperReturnType + Environment.NewLine + "Return Nothing" + Environment.NewLine + "End Function" + Environment.NewLine));

    private static void AppendPageMembers(Block document, StringBuilder sb, Dictionary<Block, int> positions)
    {
        foreach (var member in MemberBlocks(document)) {
            if (member.Type == BlockType.Functions) {
                sb.AppendLine(FunctionsCode(member));
                continue;
            }
            var (signature, body) = HelperParts(member);
            sb.AppendLine("    Function " + signature + " As " + HelperReturnType);
            if (body != null) {
                positions[body] = sb.Length; // the body's code sees the helper's parameters
                AppendCode(body, sb, positions);
            }
            EnsureNewLine(sb);
            sb.AppendLine("        Return Nothing").AppendLine("    End Function");
        }
    }

    /// <summary>Whether the fully qualified name is a type (or VB module) rather than a namespace.</summary>
    public bool IsType(string qualifiedName) => _semanticModel.Compilation.GetTypeByMetadataName(qualifiedName) != null;

    private static void AppendCode(Block block, StringBuilder sb, Dictionary<Block, int> positions)
    {
        foreach (var child in block.Children) {
            if (child is Span span) {
                if (span.Kind == SpanKind.Code) sb.Append(span.Content);
                continue;
            }
            var b = (Block)child;
            switch (b.Type) {
                case BlockType.Expression:
                    var code = string.Concat(b.Children.OfType<Span>().Where(c => c.Kind == SpanKind.Code).Select(c => c.Content));
                    if (code == "ModelType") break; // directive, not an expression
                    EnsureNewLine(sb);
                    positions[b] = sb.Length;
                    sb.Append("__W(").Append(code).AppendLine(")");
                    break;
                case BlockType.Statement:
                    EnsureNewLine(sb);
                    positions[b] = sb.Length;
                    AppendCode(b, sb, positions);
                    break;
                case BlockType.Markup:
                case BlockType.Section:
                    EnsureNewLine(sb);
                    sb.AppendLine(MarkupCall); // keeps a block body non-empty even when the markup holds no code
                    AppendCode(b, sb, positions);
                    break;
            }
        }
    }

    private static void EnsureNewLine(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.AppendLine();
    }
}
