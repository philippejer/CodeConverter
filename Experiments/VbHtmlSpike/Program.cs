using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public static class Program
{
    private const string DefaultTarget = @"C:\path\to\WebApp";
    private const bool DeleteSourceFiles = true;

    /// <summary>Usage: VbHtmlSpike [file.vbhtml | directory]...  (defaults to the Affaire views). Writes .cshtml next to each source; DeleteSourceFiles controls source deletion.</summary>
    public static async Task Main(string[] args)
    {
        var targets = args.Length > 0 ? args : new[] { DefaultTarget };
        var files = targets.SelectMany(t => Directory.Exists(t)
            ? Directory.EnumerateFiles(t, "*.vbhtml", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f))
            : new[] { t }).OrderBy(f => f).ToList();

        var contexts = new System.Collections.Generic.Dictionary<string, ProjectContext>();
        int clean = 0;
        foreach (var path in files) {
            var appDir = FindAppDir(path);
            if (!contexts.TryGetValue(appDir, out var context)) {
                contexts[appDir] = context = ProjectContext.Load(appDir);
                if (context.ViewCompilerWarning != null) Console.WriteLine("WARN " + appDir + ": " + context.ViewCompilerWarning);
            }
            var conv = new VbHtmlConverter(context);
            var cshtml = await conv.ConvertAsync(File.ReadAllText(path), path);
            var outPath = Path.ChangeExtension(path, ".cshtml");
            File.WriteAllText(outPath, cshtml, new UTF8Encoding(true));
            if (DeleteSourceFiles) File.Delete(path);
            Console.WriteLine((conv.Warnings.Count == 0 ? "OK   " : "WARN ") + path + " -> " + Path.GetFileName(outPath) + (DeleteSourceFiles ? " (source deleted)" : " (source retained)"));
            foreach (var w in conv.Warnings) Console.WriteLine("       " + w.Replace(Environment.NewLine, " "));
            if (conv.Warnings.Count == 0) clean++;
            if (Environment.GetEnvironmentVariable("VBHTML_SHOW") != null) Console.WriteLine(cshtml);
        }
        Console.WriteLine($"{clean}/{files.Count} views converted without warnings.");
    }

    /// <summary>The nearest ancestor folder containing a project file - that project's references and Option settings apply to the view.</summary>
    private static string FindAppDir(string viewPath)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(viewPath)); dir != null; dir = Path.GetDirectoryName(dir))
            if (Directory.EnumerateFiles(dir, "*.vbproj").Any() || Directory.EnumerateFiles(dir, "*.csproj").Any()) return dir;
        throw new FileNotFoundException("No .vbproj or .csproj found above " + viewPath);
    }

    private static bool IsBuildOutput(string f) => f.Contains(@"\bin\") || f.Contains(@"\obj\");
}
