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
          .AppendLine("    Sub __W(o As Object)").AppendLine("    End Sub")
          .AppendLine("    Sub __Execute()");
        var positions = new Dictionary<Block, int>(ReferenceEqualityComparer.Instance);
        AppendCode(document, sb, positions);
        EnsureNewLine(sb);
        sb.AppendLine("    End Sub").AppendLine("End Class");

        var tree = VisualBasicSyntaxTree.ParseText(sb.ToString());
        var compilation = VisualBasicCompilation.Create("__ViewScope", new[] { tree }, references, options);
        if (Environment.GetEnvironmentVariable("VBHTML_DEBUG") != null) Console.WriteLine("[SCOPE]" + Environment.NewLine + sb);
        return new ViewScope(compilation.GetSemanticModel(tree), positions);
    }

    /// <summary>Locals visible where the fragment starts, as (name, VB type) declarations. Excludes anything VB can't declare by name.</summary>
    public IEnumerable<(string Name, string VbType)> LocalsInScope(Block fragment)
    {
        if (fragment == null || !_positions.TryGetValue(fragment, out var position)) return Enumerable.Empty<(string, string)>();
        return _semanticModel.LookupSymbols(position).OfType<ILocalSymbol>()
            .Where(l => l.Locations.All(loc => loc.SourceSpan.Start < position)) // VB scopes a Dim to its whole block, but it's only usable after it
            .Where(l => !l.Type.IsAnonymousType && l.Type.TypeKind != TypeKind.Error)
            .Select(l => (l.Name, l.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
    }

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
