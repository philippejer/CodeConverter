using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;

/// <summary>
/// What the converter needs to know about the web app so view fragments are converted with real type information:
/// the app's built assemblies plus .NET Framework reference assemblies to compile against, and the project's Option settings.
/// </summary>
public class ProjectContext
{
    private static readonly string FrameworkRoot = @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework";

    /// <summary>Namespaces a Razor view gets implicitly from Views/web.config plus the VB project defaults.</summary>
    private static readonly string[] ViewImports = {
        "System", "System.Collections.Generic", "System.Linq", "Microsoft.VisualBasic",
        "System.Web.Mvc", "System.Web.Mvc.Html", "System.Web.Routing"
    };

    public IReadOnlyCollection<PortableExecutableReference> References { get; private set; }
    public VisualBasicCompilationOptions Options { get; private set; }

    /// <summary>Set when the app's views won't compile the latest C# that the converter emits (it always targets LangVersion latest).</summary>
    public string ViewCompilerWarning { get; private set; }

    public bool HasMvc => References.Any(r => r.Display != null && r.Display.EndsWith("System.Web.Mvc.dll", StringComparison.OrdinalIgnoreCase));

    public static ProjectContext Load(string appDir)
    {
        var vbproj = Directory.EnumerateFiles(appDir, "*.vbproj").FirstOrDefault()
            ?? throw new FileNotFoundException("No .vbproj found in " + appDir + " - needed for Option settings and target framework");
        var props = XDocument.Load(vbproj).Descendants().Where(e => !e.HasElements).GroupBy(e => e.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.Last().Value.Trim());
        string Prop(string name, string dflt) => props.TryGetValue(name, out var v) && v.Length > 0 ? v : dflt;

        var options = new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptionExplicit(Prop("OptionExplicit", "On") == "On")
            .WithOptionCompareText(Prop("OptionCompare", "Binary") == "Text")
            .WithOptionStrict(Prop("OptionStrict", "Off") == "On" ? OptionStrict.On : OptionStrict.Off)
            .WithOptionInfer(Prop("OptionInfer", "On") == "On")
            .WithGlobalImports(GlobalImport.Parse(ViewImports));

        var refs = new List<string>();
        refs.AddRange(FrameworkAssemblies(Prop("TargetFramework", "net48")));
        var bin = Path.Combine(appDir, "bin");
        if (Directory.Exists(bin)) refs.AddRange(Directory.EnumerateFiles(bin, "*.dll"));
        else Console.WriteLine("WARN no bin folder in " + appDir + ": build the app first so its types (e.g. the model classes) can be resolved");

        return new ProjectContext {
            ViewCompilerWarning = CheckViewCompiler(Path.Combine(appDir, "Web.config")),
            Options = options,
            References = refs.Select(r => (PortableExecutableReference)MetadataReference.CreateFromFile(r)).ToList()
        };
    }

    /// <summary>
    /// ASP.NET compiles views at runtime through the CodeDOM provider in Web.config's system.codedom section.
    /// Without the Roslyn provider (Microsoft.CodeDom.Providers.DotNetCompilerPlatform) that's the legacy C# 5 compiler,
    /// and even with it the language version defaults to the old one unless /langversion is given.
    /// </summary>
    private static string CheckViewCompiler(string webConfig)
    {
        const string fix = "add the Microsoft.CodeDom.Providers.DotNetCompilerPlatform package and a system.codedom C# compiler with compilerOptions=\"/langversion:latest\" to Web.config";
        if (!File.Exists(webConfig)) return "No Web.config: cannot check which C# version views compile with; " + fix;
        var csCompiler = XDocument.Load(webConfig).Descendants("system.codedom").Descendants("compiler")
            .FirstOrDefault(c => ((string)c.Attribute("language") ?? "").Split(';').Contains("c#", StringComparer.OrdinalIgnoreCase));
        if (csCompiler == null || !((string)csCompiler.Attribute("type") ?? "").Contains("DotNetCompilerPlatform"))
            return "Views compile with the legacy C# 5 compiler, but converted views use the latest C#: " + fix;
        if (!Regex.IsMatch((string)csCompiler.Attribute("compilerOptions") ?? "", @"/langversion:(latest|preview|\d\d|[89])", RegexOptions.IgnoreCase))
            return "Views compile with the Roslyn provider's default C# version, but converted views use the latest C#: " + fix;
        return null;
    }

    private static IEnumerable<string> FrameworkAssemblies(string targetFramework)
    {
        var m = Regex.Match(targetFramework, @"^net(\d)(\d)(\d?)$");
        if (!m.Success) throw new NotSupportedException("Only .NET Framework targets (e.g. net48) are supported by this spike, found: " + targetFramework);
        var dir = Path.Combine(FrameworkRoot, "v" + m.Groups[1] + "." + m.Groups[2] + (m.Groups[3].Length > 0 ? "." + m.Groups[3] : ""));
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Reference assemblies not found (install the targeting pack): " + dir);
        return Directory.EnumerateFiles(dir, "*.dll").Concat(Directory.EnumerateFiles(Path.Combine(dir, "Facades"), "*.dll"));
    }
}
