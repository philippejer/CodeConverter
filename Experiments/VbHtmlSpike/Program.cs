using System;
using System.IO;
using System.Threading.Tasks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        foreach (var path in args) {
            var conv = new VbHtmlConverter();
            var cshtml = await conv.ConvertAsync(File.ReadAllText(path));
            var outPath = Path.ChangeExtension(path, ".cshtml");
            File.WriteAllText(outPath, cshtml);
            Console.WriteLine("==> " + outPath);
            Console.WriteLine(cshtml);
            foreach (var w in conv.Warnings) Console.WriteLine("WARN " + w);
        }
    }
}
