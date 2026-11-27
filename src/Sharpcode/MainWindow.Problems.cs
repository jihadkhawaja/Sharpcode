using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sharpcode.Core;

namespace Sharpcode;

internal sealed record ProblemRow(CodeProblem Problem, string Location, bool IsLive)
{
    public bool IsError => Problem.IsError;
    public string Code => Problem.Code;
    public string Message => Problem.Message;
    public string Detail => $"{(IsError ? "Error" : "Warning")} {Code} · {Message}\n{Location}";
}

public partial class MainWindow
{
    private readonly DispatcherTimer csharpSyntaxTimer = new() { Interval = TimeSpan.FromMilliseconds(310) };
    private readonly ProblemAdornments problemAdornments = new();
    private CancellationTokenSource? syntaxCheck;
    private CancellationTokenSource? projectCheck;
    private CodeProblem[] liveProblems = [];
    private IReadOnlyList<CodeProblem> buildProblems = [];
    private string? projectCheckStatus;
    private bool showingSavedProblemMarks;

    private void InitializeCodeProblems()
    {
        Editor.TextArea.TextView.BackgroundRenderers.Add(problemAdornments);
        csharpSyntaxTimer.Tick += async (_, _) =>
        {
            csharpSyntaxTimer.Stop();
            await TryAsync(CheckLiveSyntaxAsync);
        };
    }

    private void QueueCSharpSyntaxCheck(bool documentChanged = false)
    {
        syntaxCheck?.Cancel();
        csharpSyntaxTimer.Stop();
        var refresh = !documentChanged || liveProblems.Length > 0 || showingSavedProblemMarks;
        liveProblems = [];
        if (activeLanguage.IsCSharp && activeTab is { } tab && tab.Document.TextLength <= DocumentFormatting.SizeLimit)
            csharpSyntaxTimer.Start();
        if (refresh)
            RefreshProblems(includeSavedMarks: !documentChanged);
    }

    private async Task CheckLiveSyntaxAsync()
    {
        if (!activeLanguage.IsCSharp || activeTab is not { } tab || tab.Document.TextLength > DocumentFormatting.SizeLimit)
            return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        syntaxCheck = cancel;
        var snapshot = tab.Document.CreateSnapshot();
        var path = tab.Path;
        var script = IsCSharpScript(tab);
        try
        {
            var found = await Task.Run(() => CSharpDiagnostics.Syntax(snapshot.Text, path, script, cancel.Token), cancel.Token);
            if (activeTab == tab && syntaxCheck == cancel && !cancel.IsCancellationRequested &&
                snapshot.Version.CompareAge(tab.Document.Version) == 0)
            {
                liveProblems = found;
                RefreshProblems();
            }
        }
        finally
        {
            if (syntaxCheck == cancel)
                syntaxCheck = null;
        }
    }

    private void RefreshProblems(bool includeSavedMarks = true)
    {
        if (Editor.Document is null)
            return;
        var active = activeTab;
        var savedHere = includeSavedMarks && active is { Path: not null, IsDirty: false }
            ? buildProblems.Where(problem => Workspace.PathComparer.Equals(problem.Path, active.Path)).ToArray() : [];
        showingSavedProblemMarks = savedHere.Length > 0;
        problemAdornments.SetProblems(Editor.TextArea.TextView, liveProblems.Concat(savedHere));

        var liveLocations = liveProblems.Select(problem => problem with { Length = 0 }).ToHashSet();
        var rows = liveProblems.Select(problem => new ProblemRow(problem, ProblemLocation(problem, "Live C# syntax"), true))
            .Concat(buildProblems.Where(problem => !liveLocations.Contains(problem))
                .Select(problem => new ProblemRow(problem, ProblemLocation(problem, "Project check"), false)))
            .OrderByDescending(row => row.IsError).ToArray();
        if (ProblemList.ItemsSource is not ProblemRow[] previous || !previous.SequenceEqual(rows))
            ProblemList.ItemsSource = rows;
        var errors = rows.Count(row => row.IsError);
        StatusProblemsButton.Content = $"Problems {ProblemCounts(errors, rows.Length - errors, " · ")}";
        StatusProblemsButton.IsVisible = activeLanguage.IsCSharp || projectCheckStatus is not null;
        ProblemsSummary.Text = projectCheckStatus ?? (activeLanguage.IsCSharp
            ? active?.Document.TextLength > DocumentFormatting.SizeLimit ? "Live syntax is paused for this large file. Check the project for build diagnostics."
            : "Live syntax for the active C# file. Check the project for compiler and analyzer diagnostics."
            : "Open a C# file for live syntax checks, or check a .NET project.");
        ToolTip.SetTip(ProblemsSummary, ProblemsSummary.Text);
    }

    private string ProblemLocation(CodeProblem problem, string source)
    {
        var file = problem.Path is { } path ? workspace?.Relative(path) ?? Path.GetFileName(path) : activeTab?.Title ?? "";
        return problem.Line > 0 ? $"{file} · {problem.Line}:{problem.Column} · {source}" : source;
    }

    private static string ProblemCounts(int errors, int warnings, string separator) =>
        $"{errors} error{(errors == 1 ? "" : "s")}{separator}{warnings} warning{(warnings == 1 ? "" : "s")}";

    private void ShowProblems(bool visible)
    {
        ProblemsPane.IsVisible = visible;
        if (visible)
        {
            OutputPane.IsVisible = false;
            RefreshProblems();
        }
    }

    private async Task CheckCSharpProjectAsync()
    {
        if (projectCheck is not null)
            return;
        var current = workspace;
        var path = activeTab?.Path;
        var candidates = await Task.Run(() => FindNearestProjects(path, current?.PhysicalRoot), lifetime.Token);
        if (candidates.Length == 0 && current is not null)
            candidates = (await current.FilesAsync(lifetime.Token)).Where(file => Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                .Take(500).Select(file => Path.GetFullPath(file, current.PhysicalRoot)).ToArray();
        var project = await PickProjectAsync(candidates, current);
        if (project is null || workspace != current || projectCheck is not null)
            return;

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromMinutes(5));
        projectCheck = cancel;
        buildProblems = [];
        projectCheckStatus = $"Checking {Path.GetFileName(project)} using saved files…";
        CheckCSharpProjectButton.IsEnabled = false;
        StopProjectCheckButton.IsVisible = true;
        ShowProblems(true);
        try
        {
            var result = await CSharpDiagnostics.CheckProjectAsync(PathIdentity.Resolve(project), cancel.Token);
            if (workspace != current || cancel.IsCancellationRequested)
                return;
            buildProblems = result.Problems;
            var errors = buildProblems.Count(problem => problem.IsError);
            var warnings = buildProblems.Count - errors;
            projectCheckStatus = $"{Path.GetFileName(project)} · {ProblemCounts(errors, warnings, ", ")} · Saved files" +
                (result.IsTruncated ? " · Results limited" : "");
            Status(result.Succeeded ? "C# project check complete" : "C# project check found errors");
        }
        catch (OperationCanceledException) when (workspace == current) { projectCheckStatus = "Project check stopped or timed out."; }
        catch (OperationCanceledException) { }
        catch (IOException error) when (workspace == current) { projectCheckStatus = error.Message; }
        finally
        {
            projectCheck = null;
            CheckCSharpProjectButton.IsEnabled = true;
            StopProjectCheckButton.IsVisible = false;
            if (!lifetime.IsCancellationRequested)
                RefreshProblems();
        }
    }

    private static string[] FindNearestProjects(string? file, string? root)
    {
        if (Path.GetExtension(file)?.Equals(".csproj", StringComparison.OrdinalIgnoreCase) == true && File.Exists(file))
            return [file];
        var folder = file is not null ? Path.GetDirectoryName(file) : root;
        var relative = root is not null && folder is not null ? Path.GetRelativePath(root, folder) : null;
        var boundary = relative is not null && !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? root : folder;
        while (folder is not null)
        {
            if (Directory.Exists(folder))
            {
                var found = Directory.EnumerateFiles(folder, "*.csproj", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                    .Order(StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
                if (found.Length > 0)
                    return found;
            }
            if (Workspace.PathComparer.Equals(folder, boundary))
                break;
            folder = Path.GetDirectoryName(folder);
        }
        return [];
    }

    private async Task<string?> PickProjectAsync(string[] projects, Workspace? current)
    {
        if (projects.Length == 1)
            return projects[0];
        if (projects.Length > 1)
        {
            var choices = projects.Select(path => new PickerItem(Path.GetFileName(path), current?.Relative(path) ?? path, FilePath: path)).ToArray();
            return (await PickerWindow.PickAsync(this, "Check C# Project", "Find a project", choices))?.FilePath;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a C# project",
            SuggestedStartLocation = await WorkspaceLocationAsync(),
            FileTypeFilter = [new FilePickerFileType("C# projects") { Patterns = ["*.csproj"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void ResetProjectCheck()
    {
        projectCheck?.Cancel();
        projectCheckStatus = null;
        buildProblems = [];
        RefreshProblems();
    }

    private async Task OpenProblemAsync(ProblemRow row)
    {
        if (row.IsLive)
            JumpTo(row.Problem.Line, row.Problem.Column);
        else if (row.Problem.Path is { } path && File.Exists(path))
            await OpenDocumentAsync(path, row.Problem.Line > 0 ? row.Problem.Line : null, row.Problem.Column);
    }

    private void OnProblems(object? sender, RoutedEventArgs e) => ShowProblems(!ProblemsPane.IsVisible);
    private async void OnCheckCSharpProject(object? sender, RoutedEventArgs e) => await TryAsync(CheckCSharpProjectAsync);
    private void OnStopProjectCheck(object? sender, RoutedEventArgs e) => projectCheck?.Cancel();
    private async void OnProblemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is ProblemRow row)
            await TryAsync(() => OpenProblemAsync(row));
    }
    private async void OnProblemKeyDown(object? sender, KeyEventArgs e)
    {
        if (ProblemList.SelectedItem is ProblemRow row)
            await RunOnEnterAsync(e, () => OpenProblemAsync(row));
    }
}
