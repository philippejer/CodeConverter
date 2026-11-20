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
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using CSSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;

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
        _helperStubsVb = ViewScope.HelperStubsVb(parsed.Document);
        _helperNames = ViewScope.MemberBlocks(parsed.Document).Where(m => m.Type == BlockType.Helper)
            .Select(m => ViewScope.HelperName(ViewScope.HelperParts(m).Signature)).ToHashSet();
        _pageMembersVb = ViewScope.PageMembersVb(parsed.Document);
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
        n is Block b && b.Type == BlockType.Expression && b.Children.OfType<Span>().Any(c => c.Kind == SpanKind.Code && VbKeyword.Is(c.Content, "ModelType"));

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
                // Drop the "@" that starts markup within VB code (@<tr>): C# Razor recognises tags in code without it.
                // Keep it for a text line (@:text), which is the same in both, and keep <text>/</text> transitions.
                bool startsTextLine = i + 1 < list.Count && list[i + 1] is Span after && after.Content.StartsWith(":");
                if (s.Kind == SpanKind.Transition && s.Content == "@" && !startsTextLine) continue;
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
            case BlockType.Functions:
                return await FunctionsAsync(b);
            case BlockType.Helper:
                return await HelperAsync(b);
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
        if (VbKeyword.StartsWith(text, "Imports ")) return "@using " + text.Substring("Imports ".Length);
        if (VbKeyword.StartsWith(text, "Inherits ")) return "@inherits " + text.Substring("Inherits ".Length);
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
        bool isCode = b.Children.OfType<Span>().Any(c => c.Kind == SpanKind.MetaCode && VbKeyword.Is(c.Content, "Code"));
        var cs = await CodeBlockAsync(b);
        if (cs == null) return "@* UNCONVERTED *@ @" + string.Concat(b.Children.OfType<Span>().Select(s => s.Content));
        if (isCode) return "@{" + Environment.NewLine + cs + "}";
        return "@" + cs.TrimStart();
    }

    /// <summary>The C# for a block of VB code interleaved with markup (e.g. an If or a helper's body), markup included.</summary>
    private async Task<string> CodeBlockAsync(Block b)
    {
        var saved = _markupPlaceholders;
        _markupPlaceholders = new List<string>();
        try {
            var vb = new StringBuilder();
            await GatherAsync(b, vb);
            var cs = await ConvertStatementsAsync(vb.ToString(), b);
            if (cs == null) return null;
            var placeholders = _markupPlaceholders;
            return Regex.Replace(cs, @"__M(\d+)\(\);\s*__E\(\);", m => placeholders[int.Parse(m.Groups[1].Value)]);
        } finally { _markupPlaceholders = saved; }
    }

    /// <summary>@Functions ... End Functions => @functions { ... }: the code is members of the page class, converted as such.</summary>
    private async Task<string> FunctionsAsync(Block b)
    {
        var members = await ConvertMembersAsync(ViewScope.FunctionsCode(b));
        if (members == null) return "@* UNCONVERTED *@ @Functions" + ViewScope.FunctionsCode(b) + "End Functions";
        return "@functions {" + Environment.NewLine + Indent(members) + "}";
    }

    /// <summary>
    /// @Helper Name(x As T) ... End Helper => @helper Name(T x) { ... }: the signature is converted as a method's,
    /// the body like any other code block (its fragments see the parameters through the view scope).
    /// </summary>
    private async Task<string> HelperAsync(Block b)
    {
        var (signature, body) = ViewScope.HelperParts(b);
        var name = ViewScope.HelperName(signature);
        var paramsVb = signature.Substring(name.Length).Trim();
        var result = await ConvertInPageClassAsync("Sub __HelperSignature" + (paramsVb.Length > 0 ? paramsVb : "()") + Environment.NewLine + "End Sub");
        var parameters = result.Success
            ? FindMethod(ParseConverted(result.ConvertedCode), "__HelperSignature")?.ParameterList.ToString()
            : null;
        if (parameters == null) { Warnings.Add("Helper signature not converted: " + signature); parameters = paramsVb; }
        var cs = body == null ? "" : await CodeBlockAsync(body) ?? "@* UNCONVERTED BODY *@" + Environment.NewLine;
        return "@helper " + name + parameters + " {" + Environment.NewLine + Indent(cs) + "}";
    }

    private static string Indent(string code) =>
        string.Concat(code.Split('\n').Select(l => l.TrimEnd('\r')).Where((l, i) => l.Length > 0 || i > 0)
            .Select(l => (l.Length > 0 ? "    " + l : l) + Environment.NewLine)).TrimEnd() + Environment.NewLine;

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
    private string _pageMembersVb = "", _helperStubsVb = "";
    private HashSet<string> _helperNames = new HashSet<string>();

    /// <param name="fragment">The view block the code comes from, so the locals of enclosing blocks can be declared. Null for code outside any block.</param>
    private async Task<string> RunAsync(string vb, Block fragment)
    {
        var preamble = _scope?.LocalsInScope(fragment).ToList() ?? new List<(string Name, string VbType)>();
        vb = string.Concat(preamble.Select(d => "Dim " + d.Name + " As " + d.VbType + Environment.NewLine)) + vb;
        var result = await ConvertInPageClassAsync(_pageMembersVb + "Sub SurroundingSub()" + Environment.NewLine + vb + Environment.NewLine + "End Sub");
        if (!result.Success) { Warnings.Add("Convert failed: " + vb + " :: " + result.GetExceptionsAsString()); return null; }
        var code = Dedent(string.Concat(FindMethod(ParseConverted(result.ConvertedCode), "SurroundingSub").Body.Statements.Select(s => s.ToFullString())));
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
    private async Task<ConversionResult> ConvertInPageClassAsync(string classMembers)
    {
        var classHeader = _context.HasMvc
            ? "MustInherit Class SurroundingClass" + Environment.NewLine + "Inherits " + ViewScope.PageBaseType(_modelVbType)
            : "Class SurroundingClass";
        var tree = VisualBasicSyntaxTree.ParseText(classHeader + Environment.NewLine + classMembers + Environment.NewLine + "End Class");
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

    /// <summary>Converts @Functions code: the C# members of the page class, except the helper stubs it was converted alongside.</summary>
    private async Task<string> ConvertMembersAsync(string membersVb)
    {
        var result = await ConvertInPageClassAsync(_helperStubsVb + membersVb);
        if (!result.Success) { Warnings.Add("Convert failed: " + membersVb + " :: " + result.GetExceptionsAsString()); return null; }
        var pageClass = ParseConverted(result.ConvertedCode).DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .First(c => c.Identifier.Text == "SurroundingClass");
        var members = pageClass.Members.Where(m => !(m is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method && _helperNames.Contains(method.Identifier.Text)));
        return Dedent(string.Concat(members.Select(m => m.ToFullString())));
    }

    /// <summary>Parses a converted file, adding its using directives (e.g. Microsoft.VisualBasic for Strings.Left) to those the view needs.</summary>
    private SyntaxNode ParseConverted(string convertedFile)
    {
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(convertedFile).GetRoot();
        foreach (var u in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>())
            _requiredUsings.Add(u.ToString().Substring("using ".Length).TrimEnd(';').Trim());
        return root;
    }

    private static Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax FindMethod(SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == name);

    private static string Dedent(string code)
    {
        var lines = code.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
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
        if (cs == null) return null;
        var block = (CSSyntax.BlockSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseStatement("{" + Environment.NewLine + cs + "}");
        var rewriter = new RazorBlockRewriter();
        var rewritten = (CSSyntax.BlockSyntax)rewriter.Visit(block);
        if (rewriter.Changed) {
            var workspace = (await ThreadSafeWorkspaceHelper.EmptyAdhocSolution.GetValueAsync()).Workspace;
            rewritten = (CSSyntax.BlockSyntax)Formatter.Format(rewritten, workspace);
            var text = rewritten.ToFullString();
            var start = rewritten.OpenBraceToken.Span.End;
            cs = Dedent(text.Substring(start - rewritten.FullSpan.Start, rewritten.CloseBraceToken.SpanStart - start));
        }
        return cs.Trim() + Environment.NewLine;
    }

    private sealed class RazorBlockRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        private CSSyntax.StatementSyntax Block(CSSyntax.StatementSyntax statement)
        {
            if (statement is CSSyntax.BlockSyntax) return statement;
            Changed = true;
            return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(statement);
        }

        public override SyntaxNode VisitIfStatement(CSSyntax.IfStatementSyntax node)
        {
            var result = (CSSyntax.IfStatementSyntax)base.VisitIfStatement(node);
            return result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitElseClause(CSSyntax.ElseClauseSyntax node)
        {
            var result = (CSSyntax.ElseClauseSyntax)base.VisitElseClause(node);
            return result.Statement is CSSyntax.IfStatementSyntax ? result : result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitForStatement(CSSyntax.ForStatementSyntax node)
        {
            var result = (CSSyntax.ForStatementSyntax)base.VisitForStatement(node);
            return result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitForEachStatement(CSSyntax.ForEachStatementSyntax node)
        {
            var result = (CSSyntax.ForEachStatementSyntax)base.VisitForEachStatement(node);
            return result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitForEachVariableStatement(CSSyntax.ForEachVariableStatementSyntax node)
        {
            var result = (CSSyntax.ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node);
            return result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitWhileStatement(CSSyntax.WhileStatementSyntax node)
        {
            var result = (CSSyntax.WhileStatementSyntax)base.VisitWhileStatement(node);
            return result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitDoStatement(CSSyntax.DoStatementSyntax node)
        {
            var result = (CSSyntax.DoStatementSyntax)base.VisitDoStatement(node);
            return result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitUsingStatement(CSSyntax.UsingStatementSyntax node)
        {
            var result = (CSSyntax.UsingStatementSyntax)base.VisitUsingStatement(node);
            return result.WithStatement(Block(result.Statement));
        }

        public override SyntaxNode VisitLockStatement(CSSyntax.LockStatementSyntax node)
        {
            var result = (CSSyntax.LockStatementSyntax)base.VisitLockStatement(node);
            return result.WithStatement(Block(result.Statement));
        }
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
