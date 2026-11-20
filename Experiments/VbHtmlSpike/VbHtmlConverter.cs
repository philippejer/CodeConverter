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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

/// <summary>Experimental VBHTML -> CSHTML converter: Razor-parses the view, converts code fragments with CodeConverter, re-emits C# Razor.</summary>
public class VbHtmlConverter
{
    public List<string> Warnings { get; } = new List<string>();
    private List<string> _markupPlaceholders;
    private readonly ProjectContext _context;

    public VbHtmlConverter(ProjectContext context)
    {
        _context = context;
    }

    /// <param name="viewPath">Locates the web.config files whose namespaces the view imports.</param>
    public async Task<string> ConvertAsync(string vbhtml, string viewPath)
    {
        var engine = new RazorTemplateEngine(new RazorEngineHost(new VBRazorCodeLanguage()));
        var parsed = engine.ParseTemplate(new StringReader(vbhtml));
        foreach (var e in parsed.ParserErrors) Warnings.Add("Parse: " + e);
        if (Environment.GetEnvironmentVariable("VBHTML_TREE") != null) Dump(parsed.Document, 0);
        _modelVbType = FindModelType(parsed.Document);
        var viewImports = FindImports(parsed.Document).ToList();
        _options = _context.OptionsForView(viewPath, viewImports);
        _scope = _context.HasMvc ? ViewScope.Build(parsed.Document, _modelVbType, _context.References, _options) : null;
        var cshtml = await ChildrenAsync(parsed.Document.Children);
        cshtml = RequiredUsingDirectives(viewPath, viewImports) + cshtml;
        cshtml = Regex.Replace(cshtml, @"\.vbhtml(?=[""'])", ".cshtml"); // view paths, e.g. Layout = "~/Views/_Layout.vbhtml"

        // Markup is copied verbatim but converted code uses the platform newline: use the source file's own convention throughout
        var newline = vbhtml.Contains("\r\n") || !vbhtml.Contains("\n") && Environment.NewLine == "\r\n" ? "\r\n" : "\n";
        cshtml = Regex.Replace(cshtml, @"\r\n|\n", newline);

        // Self-check: the output must at least parse as C# Razor
        var check = new RazorTemplateEngine(new RazorEngineHost(new CSharpRazorCodeLanguage())).ParseTemplate(new StringReader(cshtml));
        foreach (var e in check.ParserErrors) Warnings.Add("Output does not parse as C# Razor: " + e);
        return cshtml;
    }

    /// <summary>@ModelType X parses as an expression "ModelType" followed by markup holding the type up to the end of the line.</summary>
    private static string FindModelType(Block document)
    {
        var list = document.Children.ToList();
        for (int i = 0; i + 1 < list.Count; i++) {
            if (IsModelTypeDirective(list[i]) && list[i + 1] is Span next) {
                var nl = next.Content.IndexOf('\n');
                return (nl < 0 ? next.Content : next.Content.Substring(0, nl)).Trim();
            }
        }
        return null;
    }

    /// <summary>The namespaces (or "Alias = Namespace") of the view's @Imports directives.</summary>
    private static IEnumerable<string> FindImports(Block block) =>
        block.Children.OfType<Block>().SelectMany(b => b.Type == BlockType.Directive
            ? new[] { DirectiveText(b) }.Where(t => t.StartsWith("@using ")).Select(t => t.Substring("@using ".Length).Trim())
            : FindImports(b));

    private static bool IsModelTypeDirective(SyntaxTreeNode n) =>
        n is Block b && b.Type == BlockType.Expression && b.Children.OfType<Span>().Any(c => c.Kind == SpanKind.Code && c.Content == "ModelType");

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
                if (s.Kind == SpanKind.Transition && s.Content == "@") continue; // keep <text>/</text> transitions, they are the same in C# Razor
                sb.Append(s.Content);
            } else if (n is Block b) {
                // @ModelType X  => @model X (type is the rest of the following markup line)
                if (IsModelTypeDirective(b) && i + 1 < list.Count && list[i + 1] is Span next) {
                    var nl = next.Content.IndexOf('\n');
                    sb.Append("@model ").Append(await ConvertTypeAsync(_modelVbType));
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
                return IsImportsOfType(b) ? "" : DirectiveText(b);
            case BlockType.Section:
                return await SectionAsync(b);
            case BlockType.Comment:
                return string.Concat(b.Children.OfType<Span>().Select(c => c.Content)); // @* *@ same in both
            default:
                return await StatementAsync(b);
        }
    }

    /// <summary>
    /// VB can import a type (e.g. a module) to use its members unqualified. C# would need "using static", which Razor 3's @using
    /// doesn't support (it only takes a namespace) - but the converter fully qualifies those members, so the import can just go.
    /// An alias (Imports X = A.B) stays: C# allows "using X = A.B" for a type too.
    /// </summary>
    private bool IsImportsOfType(Block directive)
    {
        var text = DirectiveText(directive);
        if (!text.StartsWith("@using ")) return false;
        var imported = text.Substring("@using ".Length).Trim();
        return !imported.Contains('=') && _scope?.IsType(imported) == true;
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
        var cs = await ConvertExpressionAsync(code, b);
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
            var cs = await ConvertStatementsAsync(vb.ToString(), b);
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
    private VisualBasicCompilationOptions _options;
    private readonly HashSet<string> _requiredUsings = new HashSet<string>();
    private ViewScope _scope;

    /// <param name="fragment">The view block the code comes from, so the locals of enclosing blocks can be declared. Null for code outside any block.</param>
    private async Task<string> RunAsync(string vb, Block fragment)
    {
        var preamble = _scope?.LocalsInScope(fragment).ToList() ?? new List<(string Name, string VbType)>();
        vb = string.Concat(preamble.Select(d => "Dim " + d.Name + " As " + d.VbType + Environment.NewLine)) + vb;
        var result = await ConvertStatementsInMethodAsync(vb);
        if (!result.Success) { Warnings.Add("Convert failed: " + vb + " :: " + result.GetExceptionsAsString()); return null; }
        var code = ExtractMethodBody(result.ConvertedCode, _requiredUsings);
        foreach (var d in preamble) code = Regex.Replace(code, @"^[\w<>,.?\[\] ]+ " + d.Name + @"( = [^\r\n]*)?;[ \t]*\r?\n", "", RegexOptions.Multiline);
        if (Environment.GetEnvironmentVariable("VBHTML_DEBUG") != null) Console.WriteLine("[VB] " + vb + Environment.NewLine + "[CS] " + code);
        return code;
    }

    /// <summary>
    /// Equivalent of ProjectConversion.ConvertTextAsync for statements, except the VB project gets the app's own compilation options:
    /// ConvertTextAsync always uses default ones (e.g. Option Strict Off), but Option Strict/Compare and the imports change the conversion.
    /// Mirrors its wrapping (LanguageConversionExtensions.MakeFullCompilationUnit) and its fabricated assembly name,
    /// except the class inherits the view's page base type: like in the compiled view, every page member (Request, Session, ViewBag...) then resolves.
    /// </summary>
    private async Task<ConversionResult> ConvertStatementsInMethodAsync(string statements)
    {
        var classHeader = _context.HasMvc
            ? "MustInherit Class SurroundingClass" + Environment.NewLine + "Inherits " + ViewScope.PageBaseType(_modelVbType)
            : "Class SurroundingClass";
        var tree = VisualBasicSyntaxTree.ParseText(classHeader + Environment.NewLine + "Sub SurroundingSub()" + Environment.NewLine
            + statements + Environment.NewLine + "End Sub" + Environment.NewLine + "End Class");
        var projectId = ProjectId.CreateNewId();
        const string assemblyName = "ProjectToBeConvertedWithPartialTypes"; // internal ISymbolExtensions.ForcePartialTypesAssemblyName, as used by ConvertTextAsync
        var project = (await ThreadSafeWorkspaceHelper.EmptyAdhocSolution.GetValueAsync())
            .AddProject(projectId, assemblyName, assemblyName, LanguageNames.VisualBasic).GetProject(projectId)
            .WithCompilationOptions(_options)
            .WithParseOptions(VisualBasicParseOptions.Default)
            .WithMetadataReferences(_context.References);
        var document = project.AddDocument("CodeToConvert", tree.GetRoot(), filePath: Path.Combine(Path.GetTempPath(), "CodeToConvert.vb"));
        // Converts the whole document, not a selection: only then does the result include the using directives the code needs
        var options = new SingleConversionOptions {
            RootNamespaceOverride = string.Empty // ASP.NET compiles views outside the project's root namespace
        };
        return await ProjectConversion.ConvertSingleAsync<VBToCSConversion>(document, options);
    }

    /// <summary>The statements of the converted SurroundingSub, dedented. Adds the converted file's using directives (e.g. Microsoft.VisualBasic for Strings.Left) to <paramref name="usings"/>.</summary>
    private static string ExtractMethodBody(string convertedFile, ISet<string> usings)
    {
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(convertedFile).GetRoot();
        foreach (var u in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>())
            usings.Add(u.ToString().Substring("using ".Length).TrimEnd(';').Trim());

        var method = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First(m => m.Identifier.Text == "SurroundingSub");
        var lines = string.Concat(method.Body.Statements.Select(s => s.ToFullString()))
            .Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join(Environment.NewLine, lines.Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart())).Trim('\r', '\n') + Environment.NewLine;
    }

    /// <summary>
    /// @using directives for namespaces the converted code needs but a C# view doesn't already import
    /// (Razor defaults, web.config namespaces, and the view's own @Imports which are emitted as @using where they appear).
    /// </summary>
    private string RequiredUsingDirectives(string viewPath, IEnumerable<string> viewImports)
    {
        var alreadyImported = new HashSet<string>(_context.CSharpViewImports(viewPath).Concat(viewImports));
        foreach (var u in _requiredUsings.Where(u => u.StartsWith("static ")))
            Warnings.Add("Razor 3 has no @using static: qualify the members of " + u.Substring("static ".Length) + " by hand");
        return string.Concat(_requiredUsings.Where(u => !alreadyImported.Contains(u) && !u.StartsWith("static ")).OrderBy(u => u)
            .Select(u => "@using " + u + Environment.NewLine));
    }

    private async Task<string> ConvertStatementsAsync(string vb, Block fragment)
    {
        var cs = await RunAsync(vb, fragment);
        return cs == null ? null : cs.Trim() + Environment.NewLine;
    }

    private async Task<string> ConvertExpressionAsync(string vbExpr, Block fragment)
    {
        var cs = await RunAsync("Dim __r = " + vbExpr, fragment);
        if (cs == null) return null;
        var m = Regex.Match(cs, @"\b__r = (.*);\s*$", RegexOptions.Singleline);
        if (!m.Success) { Warnings.Add("Expression not recognised: " + vbExpr + " => " + cs); return null; }
        return m.Groups[1].Value.Trim();
    }

    private async Task<string> ConvertTypeAsync(string vbType)
    {
        var cs = await RunAsync("Dim __r As " + vbType, null);
        var m = cs == null ? null : Regex.Match(cs, @"^\s*(.*?) __r\b", RegexOptions.Singleline);
        return m != null && m.Success ? m.Groups[1].Value.Trim() : vbType;
    }
}
