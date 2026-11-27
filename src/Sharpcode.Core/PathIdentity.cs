namespace Sharpcode.Core;

public static class PathIdentity
{
    public static string Resolve(string path, bool followFinalLink = true)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var remainingLinks = 40;
        if (followFinalLink)
            return ResolveLinks(fullPath, ref remainingLinks);

        var parent = Path.GetDirectoryName(fullPath);
        return parent is null ? fullPath : Path.Combine(ResolveLinks(parent, ref remainingLinks), Path.GetFileName(fullPath));
    }

    private static string ResolveLinks(string path, ref int remainingLinks)
    {
        var root = Path.GetPathRoot(path)!;
        var resolved = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? root;
                continue;
            }

            var parent = resolved;
            resolved = Path.Combine(parent, segment);
            if (new FileInfo(resolved).LinkTarget is not { } target)
                continue;

            if (remainingLinks-- == 0)
                throw new IOException($"Too many symbolic links in {path}.");

            var targetPath = OperatingSystem.IsWindows() ? Path.GetFullPath(target, parent) : Path.Combine(parent, target);
            resolved = ResolveLinks(targetPath, ref remainingLinks);
        }
        return resolved;
    }
}
