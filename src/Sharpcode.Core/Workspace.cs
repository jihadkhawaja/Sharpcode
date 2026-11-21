using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sharpcode.Core;

public sealed record WorkspaceEntry(string Path, bool IsDirectory);
public sealed record SearchHit(string Path, int Line, int Column, string Text)
{
    public string Location => $"{System.IO.Path.GetFileName(Path)}:{Line}";

    public override string ToString() => $"{Path}:{Line}   {Text}";
}

public sealed class Workspace
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", "node_modules", "bin", "obj", ".venv", "__pycache__", "target", ".next"
    };
    private const int SearchLimit = 8 * 1024 * 1024;

    private readonly SemaphoreSlim indexGate = new(1);
    private (string[] Paths, DateTimeOffset At, int Revision) index = ([], DateTimeOffset.MinValue, 0);
    private int fileRevision;

    public Workspace(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException(Root);
    }

    public string Root { get; }
    public string Name => new DirectoryInfo(Root).Name;

    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');

    public void InvalidateFileIndex() => Interlocked.Increment(ref fileRevision);

    public string Resolve(string relativePath, bool forAgent = false)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new IOException("Use a workspace-relative path with '/' separators.");

        var segments = relativePath.Split('/');
        var current = Root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                (OperatingSystem.IsWindows() && (segment.EndsWith('.') || segment.EndsWith(' '))) ||
                ExcludedDirectories.Contains(segment) || (forAgent && IsCredentialName(segment)))
                throw new IOException($"Path is unavailable in the workspace: {relativePath}");

            current = Path.Combine(current, segment);
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Symbolic links are unavailable in the workspace: {relativePath}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        return current;
    }

    public IReadOnlyList<WorkspaceEntry> Children(string folder, CancellationToken cancellationToken = default)
    {
        var absolute = folder.Length == 0 ? Root : Resolve(folder);
        var entries = new List<WorkspaceEntry>();
        foreach (var entry in new DirectoryInfo(absolute).EnumerateFileSystemInfos("*", Enumeration()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ExcludedDirectories.Contains(entry.Name))
                continue;
            entries.Add(new(Relative(entry.FullName), entry.Attributes.HasFlag(FileAttributes.Directory)));
        }

        return entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string[]> FilesAsync(CancellationToken cancellationToken)
    {
        await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var revision = Volatile.Read(ref fileRevision);
            if (index.Revision == revision && DateTimeOffset.UtcNow - index.At < TimeSpan.FromSeconds(15))
                return index.Paths;

            var gitPaths = await GitFilesAsync(cancellationToken).ConfigureAwait(false);
            var paths = gitPaths is { Count: > 0 } ? gitPaths : WalkFiles(cancellationToken);
            index = (paths.Distinct(PathComparer).Order(StringComparer.OrdinalIgnoreCase).ToArray(), DateTimeOffset.UtcNow, revision);
            return index.Paths;
        }
        finally
        {
            indexGate.Release();
        }
    }

    public static bool IsAgentVisible(string path) => path.Split('/').All(segment => !IsCredentialName(segment));

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, bool forAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        if (!forAgent && await SearchWithRipgrepAsync(query, limit, cancellationToken).ConfigureAwait(false) is { } fast)
            return fast;

        var hits = new List<SearchHit>();
        foreach (var path in await FilesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (forAgent && !IsAgentVisible(path))
                continue;
            try
            {
                var file = Resolve(path, forAgent);
                if (!File.Exists(file) || new FileInfo(file).Length > (forAgent ? TextFiles.AgentLimit : SearchLimit))
                    continue;

                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                for (var line = 1; await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } text; line++)
                {
                    if (text.Contains('\0'))
                        break;
                    var column = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (column < 0)
                        continue;
                    hits.Add(MakeHit(path, line, column, text));
                    if (hits.Count == limit)
                        return hits;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // Unreadable, non-text, and removed files do not interrupt workspace search.
            }
        }

        return hits;
    }

    private async Task<IReadOnlyList<SearchHit>?> SearchWithRipgrepAsync(string query, int limit, CancellationToken cancellationToken)
    {
        string[] options =
        [
            "--json", "--no-config", "--hidden", "--no-follow", "--fixed-strings", "--ignore-case", "--max-filesize", "8M",
            .. ExcludedDirectories.SelectMany(name => new[] { "--iglob", $"!**/{name}/**" }),
            "--", query, "."
        ];
        using var process = new Process { StartInfo = Command("rg", Root, options) };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        using var cancellation = cancellationToken.Register(() => Stop(process));
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var results = new List<SearchHit>();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var item = json.RootElement;
                if (item.GetProperty("type").GetString() != "match")
                    continue;
                var data = item.GetProperty("data");
                if (!data.GetProperty("path").TryGetProperty("text", out var path) ||
                    !data.GetProperty("lines").TryGetProperty("text", out var lines))
                    continue;

                var text = lines.GetString() ?? "";
                var utf8Offset = data.GetProperty("submatches")[0].GetProperty("start").GetInt32();
                var column = Utf16Column(text, utf8Offset);
                results.Add(MakeHit(Relative(Path.GetFullPath(path.GetString()!, Root)),
                    data.GetProperty("line_number").GetInt32(), column, text));
                if (results.Count == limit)
                    return results;
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode is 0 or 1 || results.Count > 0 ? results : null;
        }
        finally
        {
            Stop(process);
            await errors.ConfigureAwait(false);
        }
    }

    private static int Utf16Column(string line, int utf8Offset)
    {
        var characters = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (utf8Offset <= 0)
                break;
            utf8Offset -= rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }
        return characters;
    }

    private static SearchHit MakeHit(string path, int line, int column, string text)
    {
        var start = Math.Max(0, column - 40);
        return new(path, line, column + 1, text.Substring(start, Math.Min(text.Length - start, 240)).Trim());
    }

    public static ProcessStartInfo Command(string executable, string root, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    public static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task<List<string>?> GitFilesAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = Command("git", Root, "ls-files", "--cached", "--others", "--exclude-standard", "--deduplicate", "-z")
        };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { return null; }

        using var cancellation = cancellationToken.Register(() => Stop(process));
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var paths = new List<string>();
        var entry = new StringBuilder();
        var buffer = new char[16 * 1024];
        int count;
        while ((count = await process.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character != '\0')
                {
                    entry.Append(character);
                    continue;
                }
                var path = entry.ToString();
                if (!path.Split('/').Any(ExcludedDirectories.Contains))
                    paths.Add(path);
                entry.Clear();
            }
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await error.ConfigureAwait(false);
        return process.ExitCode == 0 ? paths : null;
    }

    private List<string> WalkFiles(CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Root);
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Enumeration()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ExcludedDirectories.Contains(entry.Name))
                    continue;
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    pending.Push(entry.FullName);
                else
                    paths.Add(Relative(entry.FullName));
            }
        }
        return paths;
    }

    private static EnumerationOptions Enumeration() => new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
    };

    private static bool IsCredentialName(string segment)
    {
        var name = segment.ToLowerInvariant();
        return name is ".credentials" or ".ssh" or ".aws" or ".gnupg" or ".npmrc" or ".netrc" or
            "id_rsa" or "id_ed25519" or "credentials.json" or "secrets.json" ||
            name == ".env" || (name.StartsWith(".env.", StringComparison.Ordinal) && name is not ".env.example" and not ".env.sample") ||
            name.EndsWith(".pem", StringComparison.Ordinal) || name.EndsWith(".pfx", StringComparison.Ordinal) ||
            name.EndsWith(".key", StringComparison.Ordinal);
    }
}
