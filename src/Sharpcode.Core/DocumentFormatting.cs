using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace Sharpcode.Core;

public static class DocumentFormatting
{
    public const int SizeLimit = 2_000_000;

    public static bool Supports(string language) => language is "csharp" or "json" or "xml";

    public static IReadOnlyList<TextChange> Format(string language, string text, int indent, bool useTabs, bool script,
        CancellationToken token)
    {
        if (text.Length > SizeLimit)
            throw new IOException("This document exceeds the formatting size limit.");
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var firstBreak = text.AsSpan().IndexOfAny('\r', '\n');
        var newline = firstBreak < 0 ? "\n" : text.AsSpan(firstBreak).StartsWith("\r\n") ? "\r\n" : text[firstBreak].ToString();
        if (language == "csharp")
        {
            var syntax = ParseCSharp(text, script, token);
            using var workspace = new AdhocWorkspace();
            var options = workspace.Options.WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, useTabs)
                .WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, indent)
                .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, indent)
                .WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, newline);
            return Formatter.GetFormattedTextChanges(syntax.GetRoot(token), workspace, options, token).ToArray();
        }

        var formatted = language switch
        {
            "json" => FormatJson(text, indent, useTabs, newline),
            "xml" => FormatXml(text, useTabs ? "\t" : new string(' ', indent), newline, token),
            _ => throw new ArgumentException("This language has no built-in formatter.", nameof(language))
        };
        if (text.EndsWith('\n') || text.EndsWith('\r'))
            formatted = formatted.TrimEnd('\r', '\n') + newline;
        return formatted == text ? [] : [new(new TextSpan(0, text.Length), formatted)];
    }

    internal static SyntaxTree ParseCSharp(string text, bool script, CancellationToken token) => CSharpSyntaxTree.ParseText(text,
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest).WithKind(script ? SourceCodeKind.Script : SourceCodeKind.Regular),
        cancellationToken: token);

    private static string FormatJson(string text, int indent, bool tabs, string newline)
    {
        using var document = JsonDocument.Parse(text);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true, IndentSize = tabs ? 1 : indent, IndentCharacter = tabs ? '\t' : ' ',
            NewLine = newline == "\r" ? "\n" : newline, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
            document.RootElement.WriteTo(writer);
        var result = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return newline == "\r" ? result.Replace("\n", "\r", StringComparison.Ordinal) : result;
    }

    private static string FormatXml(string text, string indentation, string newline, CancellationToken token)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        var pending = new Stack<(XElement Element, string Indent)>();
        pending.Push((root, ""));
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            var (element, indent) = item;
            if ((string?)element.Attribute(XNamespace.Xml + "space") == "preserve" || indent.Length > 512)
                continue;
            var children = element.Nodes().ToArray();
            // Preserve mixed content and intentional spaces between inline elements.
            if (!children.Any(node => node is not XText) || children.OfType<XText>().Any(node => node is XCData ||
                !string.IsNullOrWhiteSpace(node.Value) || node.Value.AsSpan().IndexOfAny('\r', '\n') < 0))
                continue;
            var childIndent = indent + indentation;
            foreach (var child in children)
            {
                if (child is XText)
                    child.Remove();
                else
                    child.AddBeforeSelf(new XText(newline + childIndent));
                if (child is XElement next)
                    pending.Push((next, childIndent));
            }
            element.Add(new XText(newline + indent));
        }

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.None }))
            document.WriteTo(writer);
        return document.Declaration is { } declaration ? declaration + newline + output.ToString().TrimStart('\r', '\n') : output.ToString();
    }
}
