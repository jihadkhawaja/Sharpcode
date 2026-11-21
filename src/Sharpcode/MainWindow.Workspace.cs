using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow
{
    private const int ExplorerPageSize = 350;
    private CancellationTokenSource? searchCancellation;
    private CancellationTokenSource? commandCancellation;
    private int explorerRevision;
    private bool switchingWorkspace;
    private string? pendingWorkspacePath;

    private string[] RecentPaths() => (preferences.RecentWorkspaces ?? [])
        .Prepend(preferences.LastWorkspace)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => path!)
        .Distinct(Workspace.PathComparer)
        .Take(8)
        .ToArray();

    private RecentWorkspace[] RecentFolders() => RecentPaths()
        .Where(path => !Workspace.PathComparer.Equals(path, workspace?.Root))
        .Select(path => new RecentWorkspace(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name
            ? name : path, path))
        .ToArray();

    private void RefreshWelcome()
    {
        WelcomeTitle.Text = workspace?.Name ?? "Get started";
        WelcomeDescription.Text = workspace is null ? "Open a folder to edit and search a project."
            : "Find a file by name or select one in the sidebar.";
        WelcomeOpenFolder.IsVisible = workspace is null;
        WelcomeBrowse.IsVisible = workspace is not null;
        var folders = RecentFolders().Take(4).ToArray();
        RecentList.ItemsSource = folders;
        RecentPane.IsVisible = folders.Length > 0;
    }

    private async Task OpenRecentAsync()
    {
        var folders = RecentFolders();
        if (folders.Length == 0)
        {
            await OpenFolderAsync();
            return;
        }

        var actions = folders.Select(folder => new PickerItem(folder.Name, folder.Path, () => OpenRecentFolderAsync(folder.Path)))
            .Append(new("Open Folder…", null, OpenFolderAsync)).ToArray();
        var choice = await PickerWindow.PickAsync(this, "Switch Workspace", "Find a recent folder", actions);
        if (choice?.Action is { } open)
            await open();
    }

    private async Task OpenRecentFolderAsync(string path)
    {
        if (await Task.Run(() => Directory.Exists(path), lifetime.Token))
        {
            await SetWorkspaceAsync(path);
            return;
        }
        var paths = RecentPaths().Where(folder => !Workspace.PathComparer.Equals(folder, path)).ToArray();
        preferences = preferences with
        {
            RecentWorkspaces = paths,
            LastWorkspace = Workspace.PathComparer.Equals(preferences.LastWorkspace, path) ? null : preferences.LastWorkspace
        };
        RefreshWelcome();
        ShowError($"The folder is unavailable: {path}");
        await PersistPreferencesAsync();
    }

    private async Task OpenFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open workspace folder",
            SuggestedStartLocation = await WorkspaceLocationAsync()
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await SetWorkspaceAsync(path);
    }

    private async Task SetWorkspaceAsync(string path)
    {
        var next = new Workspace(path);
        if (switchingWorkspace)
        {
            pendingWorkspacePath = next.Root;
            return;
        }
        if (Workspace.PathComparer.Equals(workspace?.Root, next.Root))
            return;
        if (gitOperation is not null)
        {
            Status("Finish or stop the current Git operation before switching folders.");
            return;
        }
        if (agentCancellation is not null || agentApplying)
        {
            Status("Finish or stop the current agent operation before switching folders.");
            return;
        }
        switchingWorkspace = true;
        try
        {
            if (proposals.Count > 0 && !await ConfirmAsync("Discard proposed changes?", "Switching folders clears unapplied drafts.", "Switch folder"))
                return;
            searchCancellation?.Cancel();
            workspace = next;
            ResetProjectCheck();
            ShowGitStatus(null, null, "Reading Git status…");
            GitFilter.Text = "";
            GitCommitMessage.Text = "";
            proposals.Clear();
            ResetConversation();
            var recent = RecentPaths().Prepend(next.Root).Distinct(Workspace.PathComparer).Take(8).ToArray();
            preferences = preferences with { LastWorkspace = next.Root, RecentWorkspaces = recent };
            WorkspaceButton.Content = $"{next.Name}  ▾";
            ToolTip.SetTip(WorkspaceButton, next.Root);
            ExplorerTitle.Text = next.Name.ToUpperInvariant();
            ExplorerEmpty.IsVisible = false;
            FileTree.IsVisible = true;
            Title = $"{next.Name} · Sharpcode";
            if (activeTab is { } tab)
                UpdateBreadcrumbAndSyntax(tab);
            SelectSidebar(ExplorerPane, ExplorerButton, reveal: false);
            RefreshWelcome();
            UpdateAgentComposer();
            SearchResults.ItemsSource = null;
            SearchSummary.Text = "";
            Status($"Workspace open · {next.Root}");
            _ = TryAsync(RefreshGitAsync);
            await Task.WhenAll(
                TryAsync(RefreshExplorerAsync),
                TryAsync(LoadHistoryAsync),
                TryAsync(PersistPreferencesAsync));
        }
        finally
        {
            switchingWorkspace = false;
            if (pendingWorkspacePath is { } pending)
            {
                pendingWorkspacePath = null;
                if (!lifetime.IsCancellationRequested)
                    await SetWorkspaceAsync(pending);
            }
        }
    }

    private async Task RefreshExplorerAsync()
    {
        if (workspace is not { } current)
            return;
        current.InvalidateFileIndex();
        var revision = ++explorerRevision;
        var expanded = ExpandedFolders(FileTree.Items).ToHashSet(Workspace.PathComparer);
        var entries = await Task.Run(() => current.Children("", lifetime.Token), lifetime.Token);
        if (workspace != current || explorerRevision != revision)
            return;
        FileTree.Items.Clear();
        PopulateTree(FileTree, entries, current, expanded, 0, revision);
    }

    private static IEnumerable<string> ExpandedFolders(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (!item.IsExpanded || item.Tag is not WorkspaceEntry entry)
                continue;
            yield return entry.Path;
            foreach (var folder in ExpandedFolders(item.Items))
                yield return folder;
        }
    }

    private void PopulateTree(ItemsControl owner, IReadOnlyList<WorkspaceEntry> entries, Workspace current,
        HashSet<string> expanded, int start, int revision)
    {
        foreach (var entry in entries.Skip(start).Take(ExplorerPageSize))
        {
            var name = Path.GetFileName(entry.Path);
            var icon = new Image
            {
                Source = MaterialExplorerIcons.For(name, entry.IsDirectory),
                Height = 17,
                Width = 17
            };
            var item = new WorkspaceTreeItem
            {
                Header = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 7,
                    Children = { icon, new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis } }
                },
                Tag = entry
            };
            Automation.SetName(item, name);
            ToolTip.SetTip(item, entry.Path);
            if (entry.IsDirectory)
            {
                item.FontWeight = Avalonia.Media.FontWeight.Medium;
                var loadingIndicator = new TreeViewItem { Header = "Loading…" };
                item.Items.Add(loadingIndicator);
                var loading = false;
                item.Expanded += async (_, args) =>
                {
                    if (loading || !ReferenceEquals(args.Source, item) || !item.Items.Contains(loadingIndicator))
                        return;
                    loading = true;
                    try
                    {
                        await TryAsync(async () =>
                        {
                            var children = await Task.Run(() => current.Children(entry.Path, lifetime.Token), lifetime.Token);
                            if (workspace != current || revision != explorerRevision)
                                return;
                            item.Items.Clear();
                            PopulateTree(item, children, current, expanded, 0, revision);
                        });
                    }
                    finally { loading = false; }
                };
            }
            owner.Items.Add(item);
            if (entry.IsDirectory && expanded.Contains(entry.Path))
                item.IsExpanded = true;
        }
        var next = start + ExplorerPageSize;
        if (next < entries.Count)
        {
            var button = new Button { Content = $"Show more ({entries.Count - next:N0})" };
            var more = new TreeViewItem { Header = button, Focusable = false };
            button.Click += (_, _) =>
            {
                owner.Items.Remove(more);
                PopulateTree(owner, entries, current, expanded, next, revision);
            };
            owner.Items.Add(more);
        }
    }

    private async Task ActivateTreeEntryAsync(TreeViewItem? item)
    {
        if (workspace is not { } current || item?.Tag is not WorkspaceEntry entry)
            return;
        if (entry.IsDirectory)
            item.SetCurrentValue(TreeViewItem.IsExpandedProperty, !item.IsExpanded);
        else
            await OpenDocumentAsync(current.Resolve(entry.Path));
    }

    private void ShowExplorer() => SelectSidebar(ExplorerPane, ExplorerButton);

    private void ShowSearch()
    {
        SelectSidebar(SearchPane, SearchButton);
        if (Editor.SelectionLength is > 0 and <= 128 && !Editor.SelectedText.Contains('\n'))
            SearchInput.Text = Editor.SelectedText;
        SearchInput.Focus();
        SearchInput.SelectAll();
        if (SearchResults.Items.Count == 0 && !string.IsNullOrWhiteSpace(SearchInput.Text))
            _ = TryAsync(SearchWorkspaceAsync);
    }

    private async Task RefreshGitPaneAsync()
    {
        SelectSidebar(GitPane, GitButton);
        await RefreshGitAsync();
    }

    private void SelectSidebar(Control pane, Button button, bool reveal = true)
    {
        if (reveal)
            SetSidebarVisible(true);
        ExplorerPane.IsVisible = pane == ExplorerPane;
        SearchPane.IsVisible = pane == SearchPane;
        GitPane.IsVisible = pane == GitPane;
        if (pane != GitPane)
            CloseGitReview();
        foreach (var navigation in new[] { ExplorerButton, SearchButton, GitButton })
            navigation.Classes.Set("active", navigation == button);
    }

    private async Task SearchWorkspaceAsync()
    {
        searchCancellation?.Cancel();
        var query = SearchInput.Text?.Trim() ?? "";
        ClearSearchButton.IsVisible = query.Length > 0;
        SearchResults.ItemsSource = null;
        if (workspace is not { } current || query.Length == 0)
        {
            SearchSummary.Text = workspace is null ? "Open a folder to search its files." : "Search for text across the workspace.";
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        searchCancellation = cancel;
        SearchSummary.Text = "Searching…";
        try
        {
            await Task.Delay(220, cancel.Token);
            var results = await Task.Run(() => current.SearchAsync(query, 400, forAgent: false, cancel.Token), cancel.Token);
            if (searchCancellation != cancel || workspace != current)
                return;
            SearchResults.ItemsSource = results;
            SearchSummary.Text = results.Count == 400 ? "First 400 matching lines" : $"{results.Count:N0} matching lines";
        }
        finally
        {
            if (searchCancellation == cancel)
                searchCancellation = null;
        }
    }

    private Task OpenSearchResultAsync() =>
        workspace is { } current && SearchResults.SelectedItem is SearchHit hit
            ? OpenDocumentAsync(current.Resolve(hit.Path), hit.Line, hit.Column) : Task.CompletedTask;

    private void ToggleOutput()
    {
        OutputPane.IsVisible = !OutputPane.IsVisible;
        if (OutputPane.IsVisible)
        {
            ProblemsPane.IsVisible = false;
            CommandInput.Focus();
        }
    }

    private Task RunTypedCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(CommandInput.Text))
            return Task.CompletedTask;
        var command = CommandInput.Text.Trim();
        var root = workspace?.Root ?? Environment.CurrentDirectory;
        var start = OperatingSystem.IsWindows()
            ? Workspace.Command("powershell.exe", root, "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(command)))
            : Workspace.Command("/bin/sh", root, "-c", command);
        return RunLocalAsync(start, command);
    }

    private async Task ScanWithBusinessHarnessAsync()
    {
        if (workspace is not { } current)
        {
            Status("Open a Git workspace before scanning commits.");
            return;
        }
        var baseRef = await Dialogs.InputAsync(this, "Scan with BusinessHarness", "Git base ref to compare with HEAD", "origin/main");
        if (!string.IsNullOrWhiteSpace(baseRef))
            await RunLocalAsync(Workspace.Command("businessharness", current.Root, "scan", "--repo", current.Root,
                "--base", baseRef.Trim(), "--head", "HEAD"), $"BusinessHarness scan · {baseRef.Trim()} → HEAD");
    }

    private async Task RunLocalAsync(ProcessStartInfo start, string display)
    {
        if (commandCancellation is not null)
            return;
        OutputPane.IsVisible = true;
        ProblemsPane.IsVisible = false;
        OutputText.Text = $"$ {display}\n";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = cancel.Token.Register(() => Workspace.Stop(process));
        commandCancellation = cancel;
        RunCommand.IsEnabled = false;
        StopCommand.IsEnabled = true;
        var pending = new ConcurrentQueue<string>();
        var output = new StringBuilder(OutputText.Text);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(85) };
        void Flush()
        {
            if (pending.IsEmpty)
                return;
            while (pending.TryDequeue(out var chunk))
                output.Append(chunk);
            if (output.Length > 96_000)
                output.Remove(0, output.Length - 96_000);
            OutputText.Text = output.ToString();
            OutputText.CaretIndex = OutputText.Text.Length;
        }
        timer.Tick += (_, _) => Flush();
        timer.Start();
        try
        {
            await Task.WhenAll(RelayAsync(process.StandardOutput, pending, cancel.Token),
                RelayAsync(process.StandardError, pending, cancel.Token), process.WaitForExitAsync(cancel.Token));
            pending.Enqueue($"\nProcess exited with code {process.ExitCode}.\n");
        }
        catch (OperationCanceledException) { pending.Enqueue("\nCommand stopped.\n"); }
        finally
        {
            timer.Stop();
            Flush();
            commandCancellation = null;
            RunCommand.IsEnabled = true;
            StopCommand.IsEnabled = false;
        }
        ScheduleGitRefresh();
    }

    private static async Task RelayAsync(StreamReader reader, ConcurrentQueue<string> output, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            output.Enqueue(new string(buffer, 0, count));
            while (output.Count > 96)
                output.TryDequeue(out _);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e) => await TryAsync(OpenFolderAsync);
    private async void OnWorkspaceMenu(object? sender, RoutedEventArgs e) => await TryAsync(OpenRecentAsync);
    private async void OnOpenRecent(object? sender, RoutedEventArgs e) => await TryAsync(OpenRecentAsync);
    private async void OnRecentWorkspace(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecentWorkspace folder)
            await TryAsync(() => OpenRecentFolderAsync(folder.Path));
    }
    private void OnExplorer(object? sender, RoutedEventArgs e) => ShowExplorer();
    private void OnSearch(object? sender, RoutedEventArgs e) => ShowSearch();
    private async void OnGit(object? sender, RoutedEventArgs e) => await TryAsync(RefreshGitPaneAsync);
    private async void OnRefreshExplorer(object? sender, RoutedEventArgs e) => await TryAsync(RefreshExplorerAsync);
    private async void OnTreeOpen(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        var item = source.FindAncestorOfType<WorkspaceTreeItem>(includeSelf: true);
        await TryAsync(() => ActivateTreeEntryAsync(item));
    }
    private async void OnSearchChanged(object? sender, TextChangedEventArgs e) => await TryAsync(SearchWorkspaceAsync);
    private async void OnSearchOpen(object? sender, TappedEventArgs e) => await TryAsync(OpenSearchResultAsync);
    private async void OnTreeKeyDown(object? sender, KeyEventArgs e) =>
        await RunOnEnterAsync(e, () => ActivateTreeEntryAsync(FileTree.SelectedItem as TreeViewItem));
    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        SearchInput.Text = "";
        SearchInput.Focus();
    }
    private async void OnSearchInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (SearchResults.Items.Count == 0 || e.Key is not (Key.Down or Key.Enter))
            return;
        SearchResults.SelectedIndex = 0;
        if (e.Key == Key.Down)
        {
            SearchResults.Focus();
            e.Handled = true;
        }
        else
            await RunOnEnterAsync(e, OpenSearchResultAsync);
    }
    private async void OnSearchResultsKeyDown(object? sender, KeyEventArgs e) => await RunOnEnterAsync(e, OpenSearchResultAsync);
    private async void OnCommandKeyDown(object? sender, KeyEventArgs e) => await RunOnEnterAsync(e, RunTypedCommandAsync);
    private void OnToggleOutput(object? sender, RoutedEventArgs e) => ToggleOutput();
    private async void OnRunCommand(object? sender, RoutedEventArgs e) => await TryAsync(RunTypedCommandAsync);
    private void OnStopCommand(object? sender, RoutedEventArgs e) => commandCancellation?.Cancel();

    private async Task RunOnEnterAsync(KeyEventArgs e, Func<Task> action)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        e.Handled = true;
        await TryAsync(action);
    }

    private sealed class WorkspaceTreeItem : TreeViewItem
    {
        protected override Type StyleKeyOverride => typeof(TreeViewItem);

        // The first tap already activated the row.
        protected override void OnHeaderDoubleTapped(TappedEventArgs e) => e.Handled = true;
    }
}
