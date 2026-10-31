using System;
using System.IO;
using Xunit;

namespace ICSharpCode.CodeConverter.Tests.LanguageAgnostic;

public class ConversionResultTests
{
    [Theory]
    [InlineData("source.vb", "source.cs", true, true)]
    [InlineData("source.vbproj", "source.csproj", true, true)]
    [InlineData("source.VB", "source.CS", true, true)]
    [InlineData("source.vb", "source.cs", false, false)]
    [InlineData("source.cs", "source.vb", true, false)]
    [InlineData("source.resx", "target.resx", true, false)]
    [InlineData("source.vb", "source.vb", true, false)]
    public void WriteToFileDeletesOnlySuccessfullyConvertedVbSources(string sourceName, string targetName, bool success, bool deleteSource)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string sourcePath = Path.Combine(directory, sourceName);
            string targetPath = Path.Combine(directory, targetName);
            File.WriteAllText(sourcePath, "original");
            var result = new ConversionResult(success ? "converted" : null) {
                SourcePathOrNull = sourcePath,
                TargetPathOrNull = targetPath
            };

            result.WriteToFile();

            Assert.Equal(!deleteSource, File.Exists(sourcePath));
            Assert.Equal(success ? "converted" : "", File.ReadAllText(targetPath));
        } finally {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WriteToFilePreservesSourceWhenWritingFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string sourcePath = Path.Combine(directory, "source.vb");
            string targetPath = Path.Combine(directory, "source.cs");
            File.WriteAllText(sourcePath, "original");
            Directory.CreateDirectory(targetPath);
            var result = new ConversionResult("converted") {
                SourcePathOrNull = sourcePath,
                TargetPathOrNull = targetPath
            };

            Assert.ThrowsAny<Exception>(() => result.WriteToFile());

            Assert.Equal("original", File.ReadAllText(sourcePath));
        } finally {
            Directory.Delete(directory, true);
        }
    }
}