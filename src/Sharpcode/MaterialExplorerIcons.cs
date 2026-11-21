using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Sharpcode;

internal static class MaterialExplorerIcons
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public static Bitmap For(string name, bool directory)
    {
        var key = IconKey(name.ToLowerInvariant(), directory);
        if (Cache.TryGetValue(key, out var icon))
            return icon;
        using var asset = AssetLoader.Open(new Uri($"avares://Sharpcode/Assets/Material/{key}.png"));
        return Cache[key] = new Bitmap(asset);
    }

    internal static string IconKey(string name, bool directory)
    {
        if (directory)
            return name switch
            {
                ".github" => "folder-github",
                ".vscode" => "folder-vscode",
                "src" or "source" or "app" or "apps" => "folder-src",
                "test" or "tests" or "spec" or "specs" or "__tests__" => "folder-test",
                "doc" or "docs" or "documentation" => "folder-docs",
                "images" or "img" or "icons" => "folder-images",
                "assets" or "resources" or "external" or "lib" or "libs" => "folder-resource",
                "config" or "configs" or "properties" => "folder-config",
                "build" or "dist" or "out" or "artifacts" or "packaging" => "folder-dist",
                "scripts" or "tools" or "eng" => "folder-tools",
                _ => "folder-base"
            };

        if (name.EndsWith(".d.ts", StringComparison.Ordinal))
            return "typescript-def";
        return name switch
        {
            ".gitignore" or ".gitattributes" or ".gitmodules" or ".gitkeep" => "git",
            "license" or "license.txt" or "copying" => "license",
            ".editorconfig" => "editorconfig",
            "package.json" or "package-lock.json" or ".npmrc" => "npm",
            "nuget.config" => "nuget",
            "dockerfile" or ".dockerignore" or "docker-compose.yml" or "docker-compose.yaml" or "compose.yml" or "compose.yaml" => "docker",
            ".env" or ".env.example" => "settings",
            _ => Path.GetExtension(name) switch
            {
                ".cs" or ".csx" => "csharp",
                ".fs" or ".fsx" => "fsharp",
                ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj" => "visualstudio",
                ".ts" or ".mts" or ".cts" => "typescript",
                ".js" or ".mjs" or ".cjs" => "javascript",
                ".jsx" or ".tsx" => "react",
                ".py" or ".pyi" => "python",
                ".json" or ".jsonc" => "json",
                ".yml" or ".yaml" => "yaml",
                ".toml" => "toml",
                ".xml" or ".xaml" or ".axaml" or ".props" or ".targets" => "xml",
                ".md" or ".markdown" => "markdown",
                ".nupkg" or ".nuspec" => "nuget",
                ".sql" or ".db" or ".sqlite" => "database",
                ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" => "cpp",
                ".c" or ".h" => "c",
                ".java" => "java",
                ".kt" or ".kts" => "kotlin",
                ".go" => "go",
                ".rs" => "rust",
                ".zig" => "zig",
                ".swift" => "swift",
                ".rb" => "ruby",
                ".php" => "php",
                ".htm" or ".html" => "html",
                ".css" => "css",
                ".sass" or ".scss" => "sass",
                ".ps1" or ".psd1" or ".psm1" => "powershell",
                ".sh" or ".bash" or ".zsh" or ".bat" or ".cmd" => "console",
                ".png" or ".svg" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".icns" => "image",
                ".zip" or ".tar" or ".gz" or ".7z" => "zip",
                ".lock" => "lock",
                ".ini" or ".conf" or ".cfg" => "settings",
                ".vue" => "vue",
                ".svelte" => "svelte",
                ".graphql" or ".gql" => "graphql",
                ".pdf" => "pdf",
                _ => "document"
            }
        };
    }
}
