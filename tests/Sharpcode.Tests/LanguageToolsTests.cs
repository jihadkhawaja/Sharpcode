using Microsoft.CodeAnalysis.Text;
using Sharpcode.Core;
using TextMateSharp.Grammars;

namespace Sharpcode.Tests;

public sealed class LanguageToolsTests
{
    [Theory]
    [InlineData("Example.CS", "", "csharp")]
    [InlineData("Directory.Build.props", "", "xml")]
    [InlineData("README.markdown", "", "markdown")]
    [InlineData("Dockerfile", "", "dockerfile")]
    [InlineData("script", "#!/usr/bin/env python3\nprint('hello')", "python")]
    [InlineData(null, "{\"enabled\":true}", "json")]
    [InlineData("note.txt", "{\"enabled\":true}", "plaintext")]
    public void LanguageDetectionUsesFileTypeAndRecognizableContent(string? path, string text, string expected) =>
        Assert.Equal(expected, EditorLanguage.Detect(path, text, new RegistryOptions(ThemeName.DarkPlus)));

    [Fact]
    public void FormattersPreserveLineEndingsAndMeaningfulContent()
    {
        Assert.Equal("{\r\n  \"answer\": 42,\r\n  \"word\": \"你好\"\r\n}\r\n", Format("json", "{\"answer\":42,\"word\":\"你好\"}\r\n", 2));
        var xml = Format("xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><root><p><b>a</b> <b>b</b></p><item/></root>\n", 2);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n  <p>", xml);
        Assert.Contains("<b>a</b> <b>b</b>", xml);
        Assert.Contains("\n  <item />\n</root>\n", xml);
        Assert.Throws<System.Xml.XmlException>(() => Format("xml", "<!DOCTYPE root><root/>", 4));

        var csharp = Format("csharp", "class Demo{\r\nint Value=42;\r\n}\r\n", 2);
        Assert.Contains("\r\n  int Value = 42;\r\n", csharp);
        Assert.Empty(CSharpDiagnostics.Syntax(csharp, null, false, TestContext.Current.CancellationToken));
    }

    private static string Format(string language, string source, int indent) => SourceText.From(source)
        .WithChanges(DocumentFormatting.Format(language, source, indent, false, false, TestContext.Current.CancellationToken)).ToString();
}
