using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow
{
    private readonly DispatcherTimer gitRefreshAfterWrite = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private GitRepository? gitRepository;
    private GitStatus? gitStatus;
    private IReadOnlyList<string> gitRemotes = [];
    private GitChangeRow[] gitStagedRows = [];
    private GitChangeRow[] gitWorkingRows = [];
    private Func<Task>? gitSyncAction;
    private CancellationTokenSource? gitOperation;
    private CancellationTokenSource? gitStatusRead;
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

    private void ScheduleGitRefresh()
    {
        if (gitRepository is null && !GitPane.IsVisible)
            return;
        gitRefreshAfterWrite.Stop();
        gitRefreshAfterWrite.Start();
    }

    private async Task ReadGitStatusAsync()
    {
        gitRefreshAfterWrite.Stop();
        gitStatusRead?.Cancel();
        if (workspace is not { } current)
        {
            gitStatusRead = null;
            ShowGitStatus(null, null, "Open a folder to use source control.");
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        gitStatusRead = cancel;
        cancel.CancelAfter(TimeSpan.FromSeconds(20));
        GitRefreshButton.IsEnabled = false;
        if (gitStatus is null)
        {
            GitEmptyActions.IsEnabled = false;
            GitSummary.Text = "Reading Git status…";
        }
        try
        {
            var repository = await GitRepository.DiscoverAsync(current.Root, cancel.Token);
            if (repository is null)
            {
                if (workspace == current && gitStatusRead == cancel)
                    ShowGitStatus(null, null, "This folder is outside a Git repository.", canInitialize: true);
                return;
            }

            var status = repository.StatusAsync(cancel.Token);
            var remotes = repository.RemotesAsync(cancel.Token);
            await Task.WhenAll(status, remotes);
            if (workspace == current && gitStatusRead == cancel && !cancel.IsCancellationRequested)
            {
                ShowGitStatus(repository, await status, "", remotes: await remotes);
                await RefreshGitReviewAsync();
            }
        }
        catch (IOException exception)
        {
            if (workspace == current && gitStatusRead == cancel && !cancel.IsCancellationRequested)
                ShowGitStatus(null, null, exception.Message);
        }
        catch (OperationCanceledException)
        {
            if (workspace == current && gitStatusRead == cancel && gitOperation is null && !lifetime.IsCancellationRequested)
                ShowGitStatus(null, null, "Git status timed out. Refresh to try again.");
        }
        finally
        {
            if (gitStatusRead == cancel)
            {
                gitStatusRead = null;
                GitRefreshButton.IsEnabled = gitOperation is null;
                GitEmptyActions.IsEnabled = gitOperation is null;
            }
        }
    }

    private void ShowGitStatus(GitRepository? repository, GitStatus? status, string message, bool canInitialize = false,
        IReadOnlyList<string>? remotes = null)
    {
        var changesUpdated = !Workspace.PathComparer.Equals(gitRepository?.Root, repository?.Root) ||
            !ReferenceEquals(gitStatus, status) && (gitStatus is null || status is null || !gitStatus.Changes.SequenceEqual(status.Changes));
        gitRepository = repository;
        gitStatus = status;
        gitRemotes = remotes ?? [];
        GitDetails.IsVisible = status is not null;
        GitDetails.IsEnabled = gitOperation is null;
        GitSummary.IsVisible = status is null;
        GitEmptyActions.IsVisible = status is null;
        GitEmptyActions.IsEnabled = gitOperation is null;
        GitRefreshButton.IsEnabled = gitOperation is null && gitStatusRead is null;
        StatusBranchButton.IsVisible = status is not null;
        StatusFetchButton.IsVisible = gitRemotes.Count > 0;
        StatusBranchButton.IsEnabled = status is not null && gitOperation is null;
        StatusFetchButton.IsEnabled = gitRemotes.Count > 0 && gitOperation is null;
        GitInitButton.IsVisible = canInitialize;
        GitOpenFolderButton.IsVisible = workspace is null;
        GitCloneButton.IsVisible = workspace is null || canInitialize;
        BranchLabel.Text = "Local";
        GitSummary.Text = message;
        GitBadge.IsVisible = status is { Changes.Count: > 0 };
        GitBadgeCount.Text = status is { Changes.Count: > 99 } ? "99+" : $"{status?.Changes.Count}";
        var changeCount = status?.Changes.Count;
        var gitLabel = changeCount switch
        {
            null => "Source control",
            1 => "Source control, 1 changed file",
            _ => $"Source control, {changeCount:N0} changed files"
        };
        Automation.SetName(GitButton, gitLabel);
        ToolTip.SetTip(GitButton, gitLabel);
        if (changesUpdated)
        {
            gitStagedRows = status?.Changes.Where(change => change.IsStaged).Select(change => new GitChangeRow(change, true)).ToArray() ?? [];
            gitWorkingRows = status?.Changes.Where(change => change.IsUnstaged).Select(change => new GitChangeRow(change, false)).ToArray() ?? [];
            ApplyGitFilter();
        }

        if (repository is null || status is null)
        {
            CloseGitReview();
            return;
        }

        var branch = status.Branch ?? "Detached HEAD";
        GitBranchName.Text = branch;
        ToolTip.SetTip(GitBranchButton, $"Switch branch · {branch}\n{repository.Root}");
        var divergence = (status.Ahead == 0 ? "" : $"  ↑{status.Ahead}") + (status.Behind == 0 ? "" : $"  ↓{status.Behind}");
        BranchLabel.Text = branch + divergence;
        ToolTip.SetTip(StatusBranchButton, $"Switch branch · {branch}");
        ConfigureGitSync(status, divergence);
        UpdateCommitEnabled();
        LayoutGitGroups();
    }

    private void ConfigureGitSync(GitStatus status, string divergence)
    {
        var hasRemote = gitRemotes.Count > 0;
        var canUseUpstream = hasRemote || status.Upstream is not null;
        var canPush = status is { HasCommit: true, Branch: not null };
        (string Text, string Detail, Func<Task> Action) sync = (status.Upstream, status.Ahead, status.Behind) switch
        {
            (null, _, _) when !hasRemote => ("Add Remote…", "No remote configured", AddGitRemoteAsync),
            (null, _, _) when canPush => ("Publish Branch", "No upstream branch", PushGitAsync),
            (null, _, _) when status.Branch is null => ("Fetch…", "Detached HEAD", FetchGitAsync),
            (null, _, _) => ("Fetch…", "Commit to publish this branch", FetchGitAsync),
            ({ } upstream, > 0, > 0) when hasRemote => ("Fetch…", $"Diverged from {upstream}{divergence}", FetchGitAsync),
            ({ } upstream, _, > 0) => ($"Pull ↓{status.Behind}", $"Tracking {upstream}{divergence}", PullGitAsync),
            ({ } upstream, > 0, _) => ($"Push ↑{status.Ahead}", $"Tracking {upstream}{divergence}", PushGitAsync),
            ({ } upstream, _, _) when hasRemote => ("Fetch…", $"Tracking {upstream}", FetchGitAsync),
            ({ } upstream, _, _) => ("Add Remote…", $"Tracking {upstream}", AddGitRemoteAsync)
        };
        gitSyncAction = sync.Action;
        GitSyncButton.Content = sync.Text;
        GitSyncDetails.Text = sync.Detail;
        ToolTip.SetTip(GitSyncDetails, sync.Detail);
        ToolTip.SetTip(GitSyncButton, sync.Detail);
        GitFetchMenu.IsEnabled = hasRemote;
        GitPullMenu.IsEnabled = canUseUpstream && status.Branch is not null;
        GitPushMenu.IsEnabled = canUseUpstream && canPush;
        GitHistoryMenu.IsEnabled = status.HasCommit;
    }

    private void ApplyGitFilter()
    {
        var query = GitFilter.Text?.Trim() ?? "";
        SetGitRows(GitStaged, gitStagedRows, query);
        SetGitRows(GitWorking, gitWorkingRows, query);
        LayoutGitGroups();
    }

    private static void SetGitRows(ListBox list, GitChangeRow[] rows, string query)
    {
        var selection = (list.SelectedItem as GitChangeRow)?.Change.Path;
        var shown = query.Length == 0 ? rows : rows.Where(row => row.Change.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Change.OriginalPath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (list.ItemsSource is GitChangeRow[] previous && previous.SequenceEqual(shown))
            return;
        list.ItemsSource = shown;
        list.SelectedItem = shown.FirstOrDefault(row => row.Change.Path == selection);
    }

    private void LayoutGitGroups()
    {
        var staged = GitStaged.Items.Count;
        var working = GitWorking.Items.Count;
        GitFilter.IsVisible = gitStagedRows.Length + gitWorkingRows.Length > 0;
        GitStagedGroup.IsVisible = staged > 0;
        GitWorkingGroup.IsVisible = working > 0;
        GitStaged.IsVisible = staged > 0 && GitStagedTitle.IsChecked == true;
        GitWorking.IsVisible = working > 0 && GitWorkingTitle.IsChecked == true;
        GitDetails.RowDefinitions[4].Height = GitStaged.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        GitDetails.RowDefinitions[4].MaxHeight = GitWorking.IsVisible ? Math.Min(175, staged * 34d + 5) : double.PositiveInfinity;
        GitDetails.RowDefinitions[6].Height = GitWorking.IsVisible ? new GridLength(1.4, GridUnitType.Star) : new GridLength(0);
        GitStagedTitle.Content = GroupTitle("Staged", staged, gitStagedRows.Length, GitStagedTitle.IsChecked == true);
        GitWorkingTitle.Content = GroupTitle("Unstaged", working, gitWorkingRows.Length, GitWorkingTitle.IsChecked == true);
        GitChangesEmpty.IsVisible = staged + working == 0;
        GitChangesEmpty.Text = GitFilter.IsVisible ? "No changed files match the filter."
            : gitStatus is { HasCommit: false } ? "Save a file, then stage it to prepare your first commit." : "No changes to commit.";
        ToolTip.SetTip(GitStageAllButton, working == gitWorkingRows.Length ? "Stage all working tree changes" : "Stage all changes, including filtered-out files");
        ToolTip.SetTip(GitUnstageAllButton, staged == gitStagedRows.Length ? "Unstage all staged files" : "Unstage all files, including filtered-out files");
    }

    private static string GroupTitle(string label, int shown, int total, bool expanded) =>
        (expanded ? "▾ " : "▸ ") + label + (shown == total ? $" {total:N0}" : $" {shown:N0}/{total:N0}");

    private void UpdateCommitEnabled()
    {
        if (GitCommitButton is null || GitCommitMessage is null)
            return;
        var staged = gitStagedRows.Length;
        var hasMessage = !string.IsNullOrWhiteSpace(GitCommitMessage.Text);
        var hasConflict = gitWorkingRows.Any(row => row.Change.IsConflict);
        GitCommitButton.IsEnabled = gitOperation is null && gitStatus is not null && staged > 0 && hasMessage && !hasConflict;
        GitCommitButton.Content = staged == 0 ? "Commit" : $"Commit ({staged:N0})";
        GitCommitHint.Text = hasConflict ? "Resolve merge conflicts before committing."
            : staged == 0 ? "Stage files to include them in a commit."
            : !hasMessage ? "Enter a commit message." : $"{PrimaryLabel}Enter to commit staged files.";
        ToolTip.SetTip(GitCommitButton, GitCommitHint.Text);
    }

    private async Task RunGitAsync(string progress, string success, Func<CancellationToken, Task> action)
    {
        if (gitOperation is not null)
            return;

        var reading = gitStatusRead;
        gitStatusRead = null;
        reading?.Cancel();
        gitReviewRead?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        gitOperation = cancellation;
        GitDetails.IsEnabled = GitEmptyActions.IsEnabled = GitRefreshButton.IsEnabled = GitReviewActions.IsEnabled = false;
        StatusBranchButton.IsEnabled = StatusFetchButton.IsEnabled = false;
        GitStopButton.IsVisible = true;
        GitSummary.IsVisible = true;
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
            await DiffWindow.ShowGitHistoryAsync(this, await repository.HistoryAsync(lifetime.Token));
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
    private void OnGitFilterChanged(object? sender, TextChangedEventArgs e) => ApplyGitFilter();
    private void OnGitGroupToggled(object? sender, RoutedEventArgs e) => LayoutGitGroups();
    private async void OnGitCommit(object? sender, RoutedEventArgs e) => await TryAsync(CommitGitAsync);
    private async void OnGitStageAll(object? sender, RoutedEventArgs e) => await StageGitAsync(null, stage: true);
    private async void OnGitUnstageAll(object? sender, RoutedEventArgs e) => await StageGitAsync(null, stage: false);
    private async void OnGitRowAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is GitChangeRow row)
            await StageGitAsync(row.Change, stage: !row.Staged);
    }
    private async void OnGitSync(object? sender, RoutedEventArgs e)
    {
        if (gitSyncAction is { } action)
            await TryAsync(action);
    }
    private async void OnGitBranches(object? sender, RoutedEventArgs e) => await TryAsync(SwitchGitBranchAsync);
    private async void OnGitCreateBranch(object? sender, RoutedEventArgs e) => await TryAsync(CreateGitBranchAsync);
    private async void OnGitAddRemote(object? sender, RoutedEventArgs e) => await TryAsync(AddGitRemoteAsync);
    private async void OnGitFetch(object? sender, RoutedEventArgs e) => await TryAsync(FetchGitAsync);
    private async void OnGitPull(object? sender, RoutedEventArgs e) => await TryAsync(PullGitAsync);
    private async void OnGitPush(object? sender, RoutedEventArgs e) => await TryAsync(PushGitAsync);
    private async void OnGitHistory(object? sender, RoutedEventArgs e) => await TryAsync(ShowGitHistoryAsync);
}
