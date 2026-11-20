using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Razor;
using System.Web.Razor.Parser.SyntaxTree;
using ICSharpCode.CodeConverter;
using ICSharpCode.CodeConverter.Common;
using ICSharpCode.CodeConverter.CSharp;

/// <summary>Experimental VBHTML -> CSHTML converter: Razor-parses the view, converts code fragments with CodeConverter, re-emits C# Razor.</summary>
public class VbHtmlConverter
{
    public List<string> Warnings { get; } = new List<string>();
    private List<string> _markupPlaceholders;

    public async Task<string> ConvertAsync(string vbhtml)
    {
        var engine = new RazorTemplateEngine(new RazorEngineHost(new VBRazorCodeLanguage()));
        var parsed = engine.ParseTemplate(new StringReader(vbhtml));
        foreach (var e in parsed.ParserErrors) Warnings.Add("Parse: " + e);
        if (Environment.GetEnvironmentVariable("VBHTML_TREE") != null) Dump(parsed.Document, 0);
        var cshtml = await ChildrenAsync(parsed.Document.Children);
        cshtml = Regex.Replace(cshtml, @"\.vbhtml(?=[""'])", ".cshtml"); // view paths, e.g. Layout = "~/Views/_Layout.vbhtml"

        // Self-check: the output must at least parse as C# Razor
        var check = new RazorTemplateEngine(new RazorEngineHost(new CSharpRazorCodeLanguage())).ParseTemplate(new StringReader(cshtml));
        foreach (var e in check.ParserErrors) Warnings.Add("Output does not parse as C# Razor: " + e);
        return cshtml;
    }

    private static void Dump(SyntaxTreeNode n, int d)
    {
        var pad = new string(' ', d * 2);
        if (n is Span s) Console.WriteLine(pad + s.Kind + " [" + s.Content.Replace("\r", "").Replace("\n", "\\n") + "]");
        else if (n is Block b) { Console.WriteLine(pad + "Block " + b.Type); foreach (var c in b.Children) Dump(c, d + 1); }
    }

    private async Task<string> ChildrenAsync(IEnumerable<SyntaxTreeNode> nodes)
    {
        var sb = new StringBuilder();
        var list = nodes.ToList();
        for (int i = 0; i < list.Count; i++) {
            var n = list[i];
            if (n is Span s) {
                if (s.Kind == SpanKind.Transition) continue;
                sb.Append(s.Content);
            } else if (n is Block b) {
                // @ModelType X  => @model X (type is the rest of the following markup line)
                if (b.Type == BlockType.Expression && b.Children.OfType<Span>().Any(c => c.Kind == SpanKind.Code && c.Content == "ModelType")
                    && i + 1 < list.Count && list[i + 1] is Span next) {
                    var nl = next.Content.IndexOf('\n');
                    var vbType = (nl < 0 ? next.Content : next.Content.Substring(0, nl)).Trim();
                    _modelVbType = vbType;
                    sb.Append("@model ").Append(await ConvertTypeAsync(vbType));
                    sb.Append(nl < 0 ? "" : next.Content.Substring(nl));
                    i++;
                    continue;
                }
                sb.Append(await BlockAsync(b));
            }
        }
        return sb.ToString();
    }

    private async Task<string> BlockAsync(Block b)
    {
        switch (b.Type) {
            case BlockType.Markup:
                return await ChildrenAsync(b.Children); // transition is dropped by ChildrenAsync
            case BlockType.Expression:
                return await ExpressionAsync(b);
            case BlockType.Directive:
                return DirectiveText(b);
            case BlockType.Section:
                return await SectionAsync(b);
            case BlockType.Comment:
                return string.Concat(b.Children.OfType<Span>().Select(c => c.Content)); // @* *@ same in both
            default:
                return await StatementAsync(b);
        }
    }

    private static string DirectiveText(Block b)
    {
        var text = string.Concat(b.Children.OfType<Span>().Where(c => c.Kind != SpanKind.Transition).Select(c => c.Content));
        if (text.StartsWith("Imports ")) return "@using " + text.Substring("Imports ".Length);
        if (text.StartsWith("Inherits ")) return "@inherits " + text.Substring("Inherits ".Length);
        return "@" + text;
    }

    private async Task<string> SectionAsync(Block b)
    {
        var meta = b.Children.OfType<Span>().First(c => c.Kind == SpanKind.MetaCode).Content; // "Section Name"
        var name = meta.Substring("Section".Length).Trim();
        var body = b.Children.OfType<Block>().FirstOrDefault();
        return "@section " + name + " {" + (body == null ? "" : await ChildrenAsync(body.Children)) + "}";
    }

    private async Task<string> ExpressionAsync(Block b)
    {
        var code = string.Concat(b.Children.OfType<Span>().Where(c => c.Kind == SpanKind.Code).Select(c => c.Content));
        var explicitParens = b.Children.OfType<Span>().Any(c => c.Kind == SpanKind.MetaCode && c.Content == "(");
        var cs = await ConvertExpressionAsync(code);
        if (cs == null) return "@" + (explicitParens ? "(" + code + ")" : code);
        bool simple = Regex.IsMatch(cs, @"^[\w.]+(\[[^\]\s]*\]|\([^()\s]*\))*(\.[\w.]+(\[[^\]\s]*\]|\([^()\s]*\))*)*$");
        return explicitParens || !simple ? "@(" + cs + ")" : "@" + cs;
    }

    private async Task<string> StatementAsync(Block b)
    {
        var saved = _markupPlaceholders;
        _markupPlaceholders = new List<string>();
        try {
            bool isCode = b.Children.OfType<Span>().Any(c => c.Kind == SpanKind.MetaCode && c.Content == "Code");
            var vb = new StringBuilder();
            await GatherAsync(b, vb);
            var cs = await ConvertStatementsAsync(vb.ToString());
            if (cs == null) return "@* UNCONVERTED *@ @" + vb;
            var placeholders = _markupPlaceholders;
            cs = Regex.Replace(cs, @"__M(\d+)\(\);\s*__E\(\);", m => placeholders[int.Parse(m.Groups[1].Value)]);
            if (isCode) return "@{" + Environment.NewLine + cs + "}";
            return "@" + cs.TrimStart();
        } finally { _markupPlaceholders = saved; }
    }

    private async Task GatherAsync(Block b, StringBuilder vb)
    {
        foreach (var c in b.Children) {
            if (c is Span s) {
                if (s.Kind == SpanKind.Code) vb.Append(s.Content);
            } else if (c is Block cb) {
                if (cb.Type == BlockType.Markup || cb.Type == BlockType.Expression) {
                    var own = _markupPlaceholders; // nested conversion swaps in its own list
                    var text = await BlockAsync(cb);
                    if (cb.Type == BlockType.Expression) text = "<text>" + text + "</text>"; // output expression within a code block
                    _markupPlaceholders = own;
                    vb.Append("__M").Append(own.Count).Append("() : __E()").AppendLine(); // 2 statements force braces around the body
                    own.Add(text);
                } else await GatherAsync(cb, vb);
            }
        }
    }

    // ---- CodeConverter calls ----
    private string _modelVbType;

    /// <summary>Declarations giving the converter the type info it needs (e.g. ViewData(...) becomes an indexer).</summary>
    private IEnumerable<(string Name, string VbType)> Preamble()
    {
        yield return ("ViewData", "System.Collections.Generic.Dictionary(Of String, Object)");
        if (_modelVbType != null) yield return ("Model", _modelVbType);
    }

    private async Task<string> RunAsync(string vb)
    {
        var preamble = Preamble().ToList();
        vb = string.Concat(preamble.Select(d => "Dim " + d.Name + " As " + d.VbType + Environment.NewLine)) + vb;
        var options = new TextConversionOptions(Basic.Reference.Assemblies.Net80.References.All) { RootNamespaceOverride = string.Empty };
        var result = await ProjectConversion.ConvertTextAsync<VBToCSConversion>(vb, options);
        if (!result.Success) { Warnings.Add("Convert failed: " + vb + " :: " + result.GetExceptionsAsString()); return null; }
        var code = UnwrapBlock(result.ConvertedCode);
        foreach (var d in preamble) code = Regex.Replace(code, @"^[\w<>,.?\[\] ]+ " + d.Name + @"( = [^\r\n]*)?;[ \t]*\r?\n", "", RegexOptions.Multiline);
        if (Environment.GetEnvironmentVariable("VBHTML_DEBUG") != null) Console.WriteLine("[VB] " + vb + Environment.NewLine + "[CS] " + code);
        return code;
    }

    /// <summary>Snippet conversion wraps statements in an extra { } block - strip it.</summary>
    private static string UnwrapBlock(string cs)
    {
        var t = cs.Trim();
        if (t.StartsWith("{") && t.EndsWith("}")) t = t.Substring(1, t.Length - 2);
        var lines = t.Split(new[] { (char)10 }).Select(l => l.TrimEnd((char)13)).Select(l => l.StartsWith("    ") ? l.Substring(4) : l);
        return string.Join(Environment.NewLine, lines).Trim((char)13, (char)10) + Environment.NewLine;
    }

    private async Task<string> ConvertStatementsAsync(string vb)
    {
        var cs = await RunAsync(vb);
        return cs == null ? null : cs.Trim() + Environment.NewLine;
    }

    private async Task<string> ConvertExpressionAsync(string vbExpr)
    {
        var cs = await RunAsync("Dim __r = " + vbExpr);
        if (cs == null) return null;
        var m = Regex.Match(cs, @"\b__r = (.*);\s*$", RegexOptions.Singleline);
        if (!m.Success) { Warnings.Add("Expression not recognised: " + vbExpr + " => " + cs); return null; }
        return m.Groups[1].Value.Trim();
    }

    private async Task<string> ConvertTypeAsync(string vbType)
    {
        var cs = await RunAsync("Dim __r As " + vbType);
        var m = cs == null ? null : Regex.Match(cs, @"^\s*(.*?) __r\b", RegexOptions.Singleline);
        return m != null && m.Success ? m.Groups[1].Value.Trim() : vbType;
    }
}
