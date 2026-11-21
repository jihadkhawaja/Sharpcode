using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow
{
    private GitRepository? gitRepository;
    private GitStatus? gitStatus;
    private CancellationTokenSource? gitOperation;
    private int gitRevision;
    private bool gitWorktreeUpdating;

    private async Task CloneGitAsync()
    {
        if (gitOperation is not null)
            return;
        var address = (await Dialogs.InputAsync(this, "Clone Repository", "GitHub or other Git remote URL"))?.Trim();
        if (string.IsNullOrEmpty(address))
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose parent folder for the clone",
            SuggestedStartLocation = await WorkspaceLocationAsync()
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } parent)
            return;
        var fallback = address.TrimEnd('/', '\\').Split('/', '\\', ':')[^1];
        if (fallback.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            fallback = fallback[..^4];
        var name = (await Dialogs.InputAsync(this, "Clone Repository", "New folder name", fallback))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        if (name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Enter a valid name for the new folder.");
        var destination = Path.Combine(parent, name);
        if (Path.Exists(destination))
            throw new IOException("That folder name is already in use.");

        var cloned = false;
        SelectSidebar(GitPane, GitButton);
        await RunGitAsync("Cloning repository…", "Repository cloned", async token =>
        {
            await GitRepository.CloneAsync(address, destination, token);
            cloned = true;
        });
        if (cloned)
            await SetWorkspaceAsync(destination);
    }

    private Task RefreshGitAsync() => gitOperation is null ? ReadGitStatusAsync() : Task.CompletedTask;

    private async Task ReadGitStatusAsync()
    {
        var revision = ++gitRevision;
        if (workspace is not { } current)
        {
            ShowGitStatus(null, null, "Open a folder to use source control.");
            return;
        }

        GitDetails.IsEnabled = false;
        GitInitButton.IsEnabled = false;
        GitSummary.Text = "Reading Git status…";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var repository = await GitRepository.DiscoverAsync(current.Root, cancel.Token);
            var status = repository is null ? null : await repository.StatusAsync(cancel.Token);
            if (workspace == current && revision == gitRevision)
                ShowGitStatus(repository, status, status is null ? "No Git repository in this folder." : "", canInitialize: repository is null);
        }
        catch (IOException exception)
        {
            if (workspace == current && revision == gitRevision)
                ShowGitStatus(null, null, exception.Message);
        }
        catch (OperationCanceledException)
        {
            if (workspace == current && revision == gitRevision && !lifetime.IsCancellationRequested)
                ShowGitStatus(null, null, "Git status timed out. Refresh to try again.");
        }
    }

    private void ShowGitStatus(GitRepository? repository, GitStatus? status, string message, bool canInitialize = false)
    {
        gitRepository = repository;
        gitStatus = status;
        GitDetails.IsVisible = status is not null;
        GitDetails.IsEnabled = gitOperation is null;
        GitInitButton.IsVisible = canInitialize;
        GitInitButton.IsEnabled = gitOperation is null;
        BranchLabel.Text = "Local";
        GitSummary.Text = message;
        if (repository is null || status is null)
            return;

        var stagedSelection = (GitStaged.SelectedItem as GitChange)?.Path;
        var workingSelection = (GitWorking.SelectedItem as GitChange)?.Path;
        var staged = status.Changes.Where(change => change.IsStaged).ToArray();
        var working = status.Changes.Where(change => change.IsUnstaged).ToArray();
        GitStaged.ItemsSource = staged;
        GitWorking.ItemsSource = working;
        GitStaged.SelectedItem = staged.FirstOrDefault(change => change.Path == stagedSelection);
        GitWorking.SelectedItem = working.FirstOrDefault(change => change.Path == workingSelection);
        GitStagedTitle.Text = $"STAGED · {staged.Length:N0}";
        GitWorkingTitle.Text = $"CHANGES · {working.Length:N0}";
        GitStageAllButton.IsEnabled = working.Length > 0;
        GitUnstageAllButton.IsEnabled = staged.Length > 0;
        GitPushButton.IsEnabled = status.HasCommit && status.Branch is not null;
        GitBranchButton.Content = status.Branch ?? "Detached HEAD";
        BranchLabel.Text = (status.Branch ?? "Detached HEAD") +
            (status.Ahead == 0 ? "" : $"  ↑{status.Ahead}") + (status.Behind == 0 ? "" : $"  ↓{status.Behind}");
        var summary = status.Changes.Count switch
        {
            0 => status.HasCommit ? "Working tree is clean." : "Ready for the first commit.",
            1 => "1 changed path",
            _ => $"{status.Changes.Count:N0} changed paths"
        };
        if (status.Changes.Any(change => change.IsConflict))
            summary += " · conflicts";
        if (status.Upstream is { } upstream)
            summary += $"\nTracking {upstream}";
        if (!Workspace.PathComparer.Equals(repository.Root, workspace?.Root))
            summary = $"Repository · {Path.GetFileName(repository.Root)}\n{summary}";
        GitSummary.Text = summary;
        ToolTip.SetTip(GitSummary, repository.Root);
        UpdateCommitEnabled();
    }

    private void UpdateCommitEnabled()
    {
        if (GitCommitButton is null || GitCommitMessage is null)
            return;
        GitCommitButton.IsEnabled = !string.IsNullOrWhiteSpace(GitCommitMessage.Text) &&
            gitStatus is { } status && status.Changes.Any(change => change.IsStaged) &&
            !status.Changes.Any(change => change.IsConflict);
    }

    private async Task RunGitAsync(string progress, string success, Func<CancellationToken, Task> action)
    {
        if (gitOperation is not null)
            return;

        ++gitRevision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        gitOperation = cancellation;
        GitDetails.IsEnabled = GitInitButton.IsEnabled = false;
        GitStopButton.IsVisible = true;
        GitSummary.Text = progress;
        Status(progress);
        try
        {
            await action(cancellation.Token);
            Status(success);
        }
        catch (OperationCanceledException) { Status("Git operation stopped."); }
        catch (Exception exception) { ShowError(exception.Message); }
        finally
        {
            gitOperation = null;
            GitStopButton.IsVisible = false;
            if (!lifetime.IsCancellationRequested)
                await ReadGitStatusAsync();
        }
    }

    private Task StageGitAsync(GitChange? change, bool stage)
    {
        if (gitRepository is not { } repository || gitStatus is not { } status)
            return Task.CompletedTask;
        return stage ? RunGitAsync("Staging changes…", "Changes staged", token => repository.StageAsync(change, token))
            : RunGitAsync("Unstaging changes…", "Changes unstaged", token => repository.UnstageAsync(change, status.HasCommit, token));
    }

    private async Task CommitGitAsync()
    {
        if (gitRepository is not { } repository || !GitCommitButton.IsEnabled || GitCommitMessage.Text is not { } message)
            return;
        await RunGitAsync("Committing staged changes…", "Commit created", async token =>
        {
            await repository.CommitAsync(message, token);
            GitCommitMessage.Text = "";
        });
    }

    private async Task ShowGitDiffAsync(ListBox list, bool staged)
    {
        if (gitOperation is not null || gitRepository is not { } repository || list.SelectedItem is not GitChange change)
            return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        var current = workspace;
        var diff = await repository.DiffAsync(change, staged, cancel.Token);
        if (workspace != current || lifetime.IsCancellationRequested)
            return;
        var path = Path.GetFullPath(change.Path, repository.Root);
        var title = $"{(staged ? "Staged" : "Working Tree")} · {change.Path}";
        if (await DiffWindow.ShowGitTextAsync(this, title, diff, diff: true, canOpenFile: File.Exists(path)))
            await OpenDocumentAsync(path);
    }

    private async Task SwitchGitBranchAsync()
    {
        if (gitRepository is not { } repository || gitOperation is not null)
            return;
        var branches = await repository.BranchesAsync(lifetime.Token);
        var choices = branches.Where(branch => branch.IsRemote || branch.Name != gitStatus?.Branch)
            .Select(branch => new PickerItem(branch.Name, branch.IsRemote ? "Remote branch" : "Local branch",
                () => ChangeGitWorktreeAsync(repository, "Switching branch…", $"Switched to {branch.Name}",
                    token => repository.SwitchAsync(branch, token))))
            .Append(new("Create Branch…", Action: CreateGitBranchAsync)).ToArray();
        var selected = await PickerWindow.PickAsync(this, "Switch Branch", "Find a branch", choices);
        if (selected?.Action is { } action)
            await action();
    }

    private async Task CreateGitBranchAsync()
    {
        if (gitRepository is not { } repository || gitOperation is not null)
            return;
        var name = (await Dialogs.InputAsync(this, "Create Branch", "New branch name"))?.Trim();
        if (!string.IsNullOrEmpty(name))
            await ChangeGitWorktreeAsync(repository, "Creating branch…", $"Switched to {name}",
                token => repository.CreateBranchAsync(name, token));
    }

    private Task ChangeGitWorktreeAsync(GitRepository repository, string progress, string success,
        Func<CancellationToken, Task> action)
    {
        if (agentApplying || openTabs.Any(tab => (tab.IsDirty || tab.IsSaving) && tab.Path is not null &&
                IsUnderRepository(repository, tab.Path)))
        {
            ShowError("Finish any active file writes and save unsaved files before changing the working tree.");
            return Task.CompletedTask;
        }

        return RunGitAsync(progress, success, async token =>
        {
            gitWorktreeUpdating = true;
            UpdateEditorReadOnly();
            try
            {
                var before = await repository.HeadAsync(token);
                await action(token);
                var after = await repository.HeadAsync(token);
                if (after is not null && before != after)
                    await ReloadGitFilesAsync(repository, await repository.ChangedPathsAsync(before, after, token), token);
            }
            finally
            {
                gitWorktreeUpdating = false;
                UpdateEditorReadOnly();
            }
        });
    }

    private static bool IsUnderRepository(GitRepository repository, string path)
    {
        var relative = Path.GetRelativePath(repository.Root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private async Task ReloadGitFilesAsync(GitRepository repository, IReadOnlyList<string> changed, CancellationToken token)
    {
        workspace?.InvalidateFileIndex();
        var paths = changed.Select(path => Path.GetFullPath(path, repository.Root)).ToHashSet(Workspace.PathComparer);
        foreach (var tab in openTabs.Where(tab => tab.Path is { } path && paths.Contains(path)).ToArray())
        {
            await TryAsync(async () =>
            {
                if (!File.Exists(tab.Path))
                {
                    await CloseTabAsync(tab);
                    return;
                }
                var snapshot = await Task.Run(() => TextFiles.ReadAsync(tab.Path!, TextFiles.EditorLimit, token), token);
                if (openTabs.Contains(tab))
                {
                    tab.Reload(snapshot);
                    if (tab == activeTab)
                    {
                        UpdateBreadcrumbAndSyntax(tab);
                        UpdateEditorStatus();
                    }
                }
            });
        }
        await TryAsync(RefreshExplorerAsync);
    }

    private async Task<string?> ChooseGitRemoteAsync(GitRepository repository, string title)
    {
        var remotes = await repository.RemotesAsync(lifetime.Token);
        if (remotes.Count == 0)
        {
            ShowError("Add a remote URL from the Git actions menu first.");
            return null;
        }
        if (remotes.Count == 1)
            return remotes[0];
        return (await PickerWindow.PickAsync(this, title, "Find a remote", remotes.Select(remote => new PickerItem(remote)).ToArray()))?.Label;
    }

    private async Task AddGitRemoteAsync()
    {
        if (gitRepository is not { } repository || gitOperation is not null)
            return;
        var existing = await repository.RemotesAsync(lifetime.Token);
        var name = (await Dialogs.InputAsync(this, "Add Remote", "Remote name", existing.Contains("origin") ? "" : "origin"))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        var address = (await Dialogs.InputAsync(this, "Add Remote", "GitHub or other Git remote URL"))?.Trim();
        if (!string.IsNullOrEmpty(address))
            await RunGitAsync("Adding remote…", $"Remote added · {name}", token => repository.AddRemoteAsync(name, address, token));
    }

    private async Task PushGitAsync()
    {
        if (gitRepository is not { } repository || gitStatus is not { Branch: not null, HasCommit: true } status || gitOperation is not null)
            return;
        var remote = status.Upstream is null ? await ChooseGitRemoteAsync(repository, "Publish Branch to Remote") : null;
        if (status.Upstream is not null || remote is not null)
            await RunGitAsync("Pushing commits…", "Push complete", token => repository.PushAsync(status, remote, token));
    }

    private async Task PullGitAsync()
    {
        if (gitRepository is not { } repository || gitStatus is not { Branch: { } branch } status || gitOperation is not null)
            return;
        var remote = status.Upstream is null ? await ChooseGitRemoteAsync(repository, "Pull from Remote") : null;
        if (status.Upstream is not null || remote is not null)
            await ChangeGitWorktreeAsync(repository, "Pulling commits…", "Pull complete",
                token => repository.PullAsync(remote, branch, token));
    }

    private async Task FetchGitAsync()
    {
        if (gitRepository is not { } repository || gitOperation is not null)
            return;
        var remote = await ChooseGitRemoteAsync(repository, "Fetch Remote");
        if (remote is not null)
            await RunGitAsync("Fetching remote refs…", "Fetch complete", token => repository.FetchAsync(remote, token));
    }

    private async Task ShowGitHistoryAsync()
    {
        if (gitRepository is { } repository && gitStatus is { HasCommit: true })
            await DiffWindow.ShowGitTextAsync(this, "Recent Commits", await repository.HistoryAsync(lifetime.Token), diff: false);
        else
            Status("This branch has no commits yet.");
    }

    private async void OnGitInit(object? sender, RoutedEventArgs e)
    {
        if (workspace is { } current && gitRepository is null)
            await RunGitAsync("Initializing repository…", "Git repository initialized",
                async token => await GitRepository.InitAsync(current.Root, token));
    }

    private async void OnGitClone(object? sender, RoutedEventArgs e) => await TryAsync(CloneGitAsync);
    private async void OnRefreshGit(object? sender, RoutedEventArgs e) => await TryAsync(RefreshGitAsync);
    private void OnGitStop(object? sender, RoutedEventArgs e) => gitOperation?.Cancel();
    private void OnGitCommitTextChanged(object? sender, TextChangedEventArgs e) => UpdateCommitEnabled();
    private async void OnGitCommit(object? sender, RoutedEventArgs e) => await TryAsync(CommitGitAsync);
    private async void OnGitStageAll(object? sender, RoutedEventArgs e) => await StageGitAsync(null, stage: true);
    private async void OnGitUnstageAll(object? sender, RoutedEventArgs e) => await StageGitAsync(null, stage: false);
    private async void OnGitStageFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is GitChange change)
            await StageGitAsync(change, stage: true);
    }
    private async void OnGitUnstageFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is GitChange change)
            await StageGitAsync(change, stage: false);
    }
    private async void OnGitStagedDiff(object? sender, TappedEventArgs e) => await OpenGitRowDiffAsync(e, GitStaged, staged: true);
    private async void OnGitWorkingDiff(object? sender, TappedEventArgs e) => await OpenGitRowDiffAsync(e, GitWorking, staged: false);
    private Task OpenGitRowDiffAsync(TappedEventArgs e, ListBox list, bool staged)
    {
        if (e.Source is not Avalonia.Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null ||
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not GitChange change)
            return Task.CompletedTask;
        list.SelectedItem = change;
        return TryAsync(() => ShowGitDiffAsync(list, staged));
    }
    private async void OnGitStagedKeyDown(object? sender, KeyEventArgs e) => await RunOnEnterAsync(e, () => ShowGitDiffAsync(GitStaged, staged: true));
    private async void OnGitWorkingKeyDown(object? sender, KeyEventArgs e) => await RunOnEnterAsync(e, () => ShowGitDiffAsync(GitWorking, staged: false));
    private async void OnGitBranches(object? sender, RoutedEventArgs e) => await TryAsync(SwitchGitBranchAsync);
    private async void OnGitCreateBranch(object? sender, RoutedEventArgs e) => await TryAsync(CreateGitBranchAsync);
    private async void OnGitAddRemote(object? sender, RoutedEventArgs e) => await TryAsync(AddGitRemoteAsync);
    private async void OnGitFetch(object? sender, RoutedEventArgs e) => await TryAsync(FetchGitAsync);
    private async void OnGitPull(object? sender, RoutedEventArgs e) => await TryAsync(PullGitAsync);
    private async void OnGitPush(object? sender, RoutedEventArgs e) => await TryAsync(PushGitAsync);
    private async void OnGitHistory(object? sender, RoutedEventArgs e) => await TryAsync(ShowGitHistoryAsync);
}
