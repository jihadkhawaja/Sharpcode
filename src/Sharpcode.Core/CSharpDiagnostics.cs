using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Sharpcode.Core;

public sealed record CodeProblem(string? Path, int Line, int Column, int Length, bool IsError, string Code, string Message);

public sealed record ProjectCheckResult(bool Succeeded, IReadOnlyList<CodeProblem> Problems, bool IsTruncated);

public static partial class CSharpDiagnostics
{
    public const int ProblemLimit = 1_000;

    public static CodeProblem[] Syntax(string text, string? path, bool script, CancellationToken token) =>
        DocumentFormatting.ParseCSharp(text, script, token).GetDiagnostics(token)
            .Where(item => item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Take(ProblemLimit).Select(item =>
            {
                var start = item.Location.GetLineSpan().StartLinePosition;
                return new CodeProblem(path, start.Line + 1, start.Character + 1, item.Location.SourceSpan.Length,
                    item.Severity == DiagnosticSeverity.Error, item.Id, item.GetMessage());
            }).ToArray();

    public static async Task<ProjectCheckResult> CheckProjectAsync(string project, CancellationToken token)
    {
        var folder = System.IO.Path.GetDirectoryName(project)!;
        var start = Workspace.Command("dotnet", folder, "build", project, "--nologo", "--tl:off", "--verbosity", "minimal",
            "--no-incremental", "-p:GenerateFullPaths=true");
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        start.Environment["VSLANG"] = "1033";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception error) { throw new IOException("Install a .NET SDK and make dotnet available on PATH to check a project.", error); }
        using var registration = token.Register(() => Workspace.Stop(process));
        var stdout = ReadProblemsAsync(process.StandardOutput, folder, token);
        var stderr = ReadProblemsAsync(process.StandardError, folder, token);
        await Task.WhenAll(process.WaitForExitAsync(token), stdout, stderr).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var output = await stdout.ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        var problems = output.Problems.Concat(errors.Problems).Distinct().Take(ProblemLimit + 1).ToArray();
        if (process.ExitCode != 0 && !problems.Any(problem => problem.IsError))
            problems = [new(null, 0, 0, 0, true, "Build", errors.LastLine ?? output.LastLine ?? $"dotnet exited with code {process.ExitCode}."), .. problems];
        return new(process.ExitCode == 0, problems.Take(ProblemLimit).ToArray(),
            output.IsTruncated || errors.IsTruncated || problems.Length > ProblemLimit);
    }

    private static async Task<(HashSet<CodeProblem> Problems, bool IsTruncated, string? LastLine)> ReadProblemsAsync(
        StreamReader stream, string folder, CancellationToken token)
    {
        var problems = new HashSet<CodeProblem>();
        string? lastLine = null;
        var truncated = false;
        while (await stream.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("Time Elapsed", StringComparison.Ordinal) &&
                !line.StartsWith("Build ", StringComparison.Ordinal) && !line.TrimStart().StartsWith("0 Warning", StringComparison.Ordinal) &&
                !line.TrimStart().StartsWith("0 Error", StringComparison.Ordinal))
                lastLine = line[..Math.Min(line.Length, 500)].Trim();
            if (line.Length > 8_192 || ReadProblem(line, folder) is not { } problem || problems.Contains(problem))
                continue;
            if (problems.Count < ProblemLimit)
                problems.Add(problem);
            else
                truncated = true;
        }
        return (problems, truncated, lastLine);
    }

    private static CodeProblem? ReadProblem(string line, string folder)
    {
        var match = BuildLine().Match(line.Trim());
        if (!match.Success)
            return null;
        var location = SourceLocation().Match(match.Groups["origin"].Value);
        var path = location.Success ? location.Groups["file"].Value : match.Groups["origin"].Value;
        int.TryParse(location.Groups["line"].Value, out var lineNumber);
        int.TryParse(location.Groups["column"].Value, out var column);
        var fullPath = location.Success || System.IO.Path.IsPathFullyQualified(path) ? System.IO.Path.GetFullPath(path, folder) : null;
        return new(fullPath, lineNumber, Math.Max(1, column), 0, match.Groups["severity"].Value == "error",
            match.Groups["code"].Value, match.Groups["message"].Value);
    }

    [GeneratedRegex(@"^(?<origin>.*?)\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.+?)(?:\s+\[[^\]]+\.(?:[a-z]+proj|slnx?)(?:::[^\]]+)?\])?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BuildLine();

    [GeneratedRegex(@"^(?<file>.+)\((?<line>\d+)(?:,(?<column>\d+)(?:,\d+,\d+)?)?\)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SourceLocation();
}
