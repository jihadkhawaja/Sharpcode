using TextMateSharp.Grammars;

namespace Sharpcode;

internal sealed record EditorLanguage(string Id, string Name)
{
    public static EditorLanguage PlainText { get; } = new("plaintext", "Plain Text");
    public bool IsMarkdown => Id == "markdown";
    public bool IsCSharp => Id == "csharp";

    public static string Detect(string? path, string start, RegistryOptions registry)
    {
        var name = Path.GetFileName(path)?.ToLowerInvariant();
        var extension = Path.GetExtension(name);
        var byName = name switch
        {
            "dockerfile" or "containerfile" => "dockerfile",
            "makefile" or "gnumakefile" => "makefile",
            ".bashrc" or ".zshrc" or ".profile" => "shellscript",
            ".editorconfig" or ".gitconfig" => "ini",
            _ => null
        };
        if (byName is not null)
            return byName;
        var byExtension = extension switch
        {
            ".cs" or ".csx" => "csharp",
            ".md" or ".markdown" => "markdown",
            ".json" => "json",
            ".xml" or ".xsd" or ".xsl" or ".xaml" or ".axaml" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or
                ".slnx" or ".plist" or ".svg" => "xml",
            ".txt" => "plaintext",
            { Length: > 0 } => registry.GetLanguageByExtension(extension)?.Id,
            _ => null
        };
        if (byExtension is not null)
            return byExtension;

        var firstBreak = start.AsSpan().IndexOfAny('\r', '\n');
        if (start.StartsWith("#!", StringComparison.Ordinal))
        {
            var command = (firstBreak < 0 ? start : start[..firstBreak])[2..].Split([' ', '\t', '/'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in command)
            {
                var script = word switch
                {
                    "sh" or "bash" or "zsh" or "dash" or "ksh" => "shellscript",
                    "python" or "python2" or "python3" => "python",
                    "node" or "nodejs" or "deno" => "javascript",
                    "pwsh" or "powershell" => "powershell",
                    "ruby" => "ruby",
                    "perl" => "perl",
                    _ => null
                };
                if (script is not null)
                    return script;
            }
        }
        var trimmed = start.AsSpan().TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.Ordinal))
            return "xml";
        if (trimmed.StartsWith("{") && (trimmed[1..].TrimStart().StartsWith("\"") || trimmed[1..].TrimStart().StartsWith("}")))
            return "json";
        return PlainText.Id;
    }
}
