using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow : Window
{
    private static readonly KeyModifiers Primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
    private static readonly string PrimaryLabel = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim settingsWrites = new(1, 1);
    private Preferences preferences;
    private Workspace? workspace;
    private bool confirmedClose;
    private bool checkingClose;
    private GridLength sidebarWidth = new(248);
    private GridLength agentWidth = new(352);
    private bool preferAgentInCompactLayout;
    private Key? chord;
    private DateTimeOffset chordAt;

    public MainWindow() : this(null) { }

    internal MainWindow(string? initialPath)
    {
        preferences = Preferences.Load();
        InitializeComponent();
        InitializeEditor();
        DiffWindow.AddDiffHighlighting(GitDiffEditor);
        ApplyAppearance(new(CurrentAppearance, preferences.FontSize, null));
        InitializeAgent();
        QuickShortcut.Text = BrowseShortcut.Text = $"{PrimaryLabel}P";
        OpenFolderShortcut.Text = $"{PrimaryLabel}K {PrimaryLabel}O";
        OpenFileShortcut.Text = $"{PrimaryLabel}O";
        NewFileShortcut.Text = $"{PrimaryLabel}N";
        RefreshWelcome();
        UpdateSearchScope();
        UpdateDockLayout(Width);
        Workbench.SizeChanged += (_, args) => UpdateDockLayout(args.NewSize.Width);
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        Closing += OnWindowClosing;
        gitRefreshAfterWrite.Tick += async (_, _) =>
        {
            gitRefreshAfterWrite.Stop();
            await TryAsync(RefreshGitAsync);
        };
        Activated += async (_, _) =>
        {
            if (workspace is not null && GitPane.IsVisible && Sidebar.IsVisible && gitStatusRead is null)
                await TryAsync(RefreshGitAsync);
        };
        Closed += (_, _) =>
        {
            gitRefreshAfterWrite.Stop();
            lifetime.Cancel();
            ClearMarkdownPreview();
            csharpSyntaxTimer.Stop();
            syntax?.Dispose();
            editorBackground?.Dispose();
            http.Dispose();
        };
        Opened += async (_, _) => await TryAsync(async () =>
        {
            loadingBackground = TryAsync(LoadBackgroundAsync);
            if (initialPath is not null)
            {
                var path = Path.GetFullPath(initialPath);
                if (Directory.Exists(path))
                    await SetWorkspaceAsync(path);
                else
                {
                    var parent = Path.GetDirectoryName(path);
                    if (parent is not null && Directory.Exists(parent))
                        await SetWorkspaceAsync(parent);
                    await OpenDocumentAsync(path);
                }
            }
            else if (Directory.Exists(preferences.LastWorkspace))
                await SetWorkspaceAsync(preferences.LastWorkspace!);
        });
    }

    private void Status(string text)
    {
        StatusText.Text = text;
        ToolTip.SetTip(StatusText, text);
    }

    private void ShowError(string message)
    {
        Status(message);
        ErrorText.Text = message;
        ErrorBanner.IsVisible = true;
    }

    private void OnDismissError(object? sender, RoutedEventArgs e) => ErrorBanner.IsVisible = false;

    private async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task PersistPreferencesAsync()
    {
        await settingsWrites.WaitAsync();
        try { await preferences.SaveAsync(); }
        finally { settingsWrites.Release(); }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (confirmedClose)
            return;
        e.Cancel = true;
        if (checkingClose)
            return;

        checkingClose = true;
        try
        {
            if (openTabs.Any(tab => tab.IsSaving) || agentApplying || gitWorktreeUpdating)
            {
                Status("Wait for the current file write to finish.");
                return;
            }
            if (proposals.Count > 0 && !await ConfirmAsync("Discard proposed changes?", "Unapplied agent drafts will be cleared.", "Discard"))
                return;
            foreach (var tab in openTabs.Where(tab => tab.IsDirty).ToArray())
            {
                var decision = await UnsavedAsync(tab);
                if (decision is null or "Cancel" || (decision == "Save" && !await SaveTabAsync(tab)))
                    return;
            }

            await PersistPreferencesAsync();
            confirmedClose = true;
            Close();
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { checkingClose = false; }
    }

    private Task<string?> UnsavedAsync(EditorTab tab) => Dialogs.ChooseAsync(this, "Save changes?",
        $"Changes to {tab.Title} are unsaved.", "Save", "Discard", "Cancel");

    private async Task<bool> ConfirmAsync(string title, string message, string action) =>
        await Dialogs.ChooseAsync(this, title, message, action, "Cancel") == action;

    private void OnToggleAgent(object? sender, RoutedEventArgs e) => SetAgentVisible(!AgentPane.IsVisible);
    private void OnToggleSidebar(object? sender, RoutedEventArgs e) => SetSidebarVisible(!Sidebar.IsVisible);

    private void SetAgentVisible(bool visible)
    {
        preferAgentInCompactLayout = visible;
        preferences = preferences with { ShowAgent = visible };
        UpdateDockLayout(Workbench.Bounds.Width);
    }

    private void SetSidebarVisible(bool visible)
    {
        preferAgentInCompactLayout = !visible;
        preferences = preferences with { ShowSidebar = visible };
        UpdateDockLayout(Workbench.Bounds.Width);
    }

    private void UpdateDockLayout(double width)
    {
        var sidebar = preferences.ShowSidebar;
        var agent = preferences.ShowAgent;
        if (sidebar && agent && width is > 0 and < 1100)
        {
            sidebar = !preferAgentInCompactLayout;
            agent = preferAgentInCompactLayout;
        }

        UpdateDock(Sidebar, SidebarSplitter, 0, 1, sidebar, ref sidebarWidth, 190, 320);
        UpdateDock(AgentPane, AgentSplitter, 4, 3, agent, ref agentWidth, 280, 440);
        SidebarToggle.Classes.Set("active", sidebar);
        AgentToggle.Classes.Set("active", agent);
        SetPanelToggleLabel(SidebarToggle, sidebar ? "Hide sidebar" : "Show sidebar", $"{PrimaryLabel}B");
        SetPanelToggleLabel(AgentToggle, agent ? "Hide coding agent" : "Show coding agent",
            agent ? null : OperatingSystem.IsMacOS() ? "⌃⌘I" : "Ctrl+Alt+I");
    }

    private static void SetPanelToggleLabel(Button button, string action, string? shortcut)
    {
        Automation.SetName(button, action);
        ToolTip.SetTip(button, shortcut is null ? action : $"{action} ({shortcut})");
    }

    private void UpdateDock(Control pane, GridSplitter splitter, int column, int divider, bool visible,
        ref GridLength savedWidth, double minimum, double maximum)
    {
        var definition = Workbench.ColumnDefinitions[column];
        definition.MinWidth = visible ? minimum : 0;
        definition.MaxWidth = maximum;
        if (!visible && pane.IsVisible)
            savedWidth = definition.Width;
        if (pane.IsVisible != visible)
            definition.Width = visible ? savedWidth : new GridLength(0);
        Workbench.ColumnDefinitions[divider].Width = new GridLength(visible ? 4 : 0);
        pane.IsVisible = splitter.IsVisible = visible;
    }

    private async void OnShortcut(object? sender, KeyEventArgs e)
    {
        var primary = e.KeyModifiers == Primary;
        var shifted = e.KeyModifiers == (Primary | KeyModifiers.Shift);
        var control = e.KeyModifiers == KeyModifiers.Control;
        var controlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        var alt = e.KeyModifiers == (Primary | KeyModifiers.Alt);
        var inEditor = Editor.TextArea.IsKeyboardFocusWithin;
        Func<Task>? action = null;

        if (chord == Key.K && DateTimeOffset.UtcNow - chordAt < TimeSpan.FromSeconds(2) && primary && e.Key == Key.O)
            action = OpenFolderAsync;
        chord = null;
        if (action is null && primary && e.Key == Key.K)
        {
            chord = Key.K;
            chordAt = DateTimeOffset.UtcNow;
            Status($"{PrimaryLabel}K pressed. {PrimaryLabel}O opens a folder.");
            e.Handled = true;
            return;
        }

        action ??= e.Key switch
        {
            Key.F1 => () => ShowPickerAsync(true),
            Key.P when shifted => () => ShowPickerAsync(true),
            Key.P when primary => () => ShowPickerAsync(false),
            Key.O when primary => OpenFileAsync,
            Key.N when primary => Immediate(NewFile),
            Key.S when shifted => () => SaveActiveAsync(forcePicker: true),
            Key.S when primary => () => SaveActiveAsync(),
            Key.W when primary && GitReviewPane.IsVisible => Immediate(DismissGitReview),
            Key.W when primary => () => CloseTabAsync(activeTab),
            Key.Tab when control => Immediate(() => CycleTabs(1)),
            Key.Tab when controlShift => Immediate(() => CycleTabs(-1)),
            Key.F when shifted => Immediate(ShowSearch),
            Key.F when primary && inEditor => Immediate(ShowFind),
            Key.F when OperatingSystem.IsMacOS() && alt && inEditor => Immediate(ShowReplace),
            Key.H when !OperatingSystem.IsMacOS() && primary && inEditor => Immediate(ShowReplace),
            Key.G when control => GoToLineAsync,
            Key.B when primary => Immediate(() => SetSidebarVisible(!Sidebar.IsVisible)),
            Key.OemComma when primary => EditAppearanceAsync,
            Key.F when e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt) => FormatDocumentAsync,
            Key.V when shifted && activeLanguage.IsMarkdown => Immediate(ToggleMarkdownPreview),
            Key.E when shifted => Immediate(ShowExplorer),
            Key.G when controlShift => RefreshGitPaneAsync,
            Key.Enter when primary && GitCommitMessage.IsKeyboardFocusWithin => CommitGitAsync,
            Key.I when e.KeyModifiers == (Primary | (OperatingSystem.IsMacOS() ? KeyModifiers.Control : KeyModifiers.Alt)) =>
                Immediate(FocusAgent),
            Key.Oem3 when control => Immediate(ToggleOutput),
            Key.OemQuestion when primary && inEditor => Immediate(ToggleComment),
            Key.Space when alt && inEditor => SuggestAsync,
            Key.Tab when e.KeyModifiers == KeyModifiers.None && inEditor && suggestion is not null => Immediate(AcceptSuggestion),
            Key.Escape when GitReviewPane.IsVisible && (GitReviewPane.IsKeyboardFocusWithin || GitPane.IsKeyboardFocusWithin) =>
                Immediate(DismissGitReview),
            Key.Escape when suggestion is not null => Immediate(DismissSuggestion),
            Key.OemPlus when primary || shifted => Immediate(() => ChangeFontSize(1)),
            Key.OemMinus when primary => Immediate(() => ChangeFontSize(-1)),
            Key.D0 when primary => Immediate(() => ChangeFontSize(0)),
            _ => null
        };

        if (action is null)
            return;
        e.Handled = true;
        await TryAsync(action);
    }

    private static Func<Task> Immediate(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    private async Task ShowPickerAsync(bool commands)
    {
        var actions = new PickerItem[]
        {
            new("Open Folder", Action: OpenFolderAsync, Shortcut: $"{PrimaryLabel}K {PrimaryLabel}O"),
            new("Open File", Action: OpenFileAsync, Shortcut: $"{PrimaryLabel}O"),
            new("New File", Action: Immediate(NewFile), Shortcut: $"{PrimaryLabel}N"),
            new("Open Recent Workspace", Action: OpenRecentAsync),
            new("Save All Files", Action: SaveAllAsync),
            new("Change File Language", Action: ChooseLanguageAsync),
            new("Format Document", Action: FormatDocumentAsync, Shortcut: "Shift+Alt+F"),
            new("Toggle Markdown Preview", Action: Immediate(ToggleMarkdownPreview), Shortcut: $"{PrimaryLabel}Shift+V"),
            new("Show Problems", Action: Immediate(() => ShowProblems(true))),
            new("Check C# Project", Action: CheckCSharpProjectAsync),
            new("Search Workspace", Action: Immediate(ShowSearch), Shortcut: $"{PrimaryLabel}Shift+F"),
            new("Show File Explorer", Action: Immediate(ShowExplorer), Shortcut: $"{PrimaryLabel}Shift+E"),
            new("Show Source Control", Action: RefreshGitPaneAsync, Shortcut: "Ctrl+Shift+G"),
            new("Clone Git Repository", Action: CloneGitAsync),
            new("Scan Commits with BusinessHarness", Action: ScanWithBusinessHarnessAsync),
            new("Toggle Local Commands", Action: Immediate(ToggleOutput), Shortcut: "Ctrl+`"),
            new("Appearance Settings", Action: EditAppearanceAsync, Shortcut: $"{PrimaryLabel},"),
            new("Go to Line", Action: GoToLineAsync, Shortcut: "Ctrl+G"),
            new("Find in File", Action: Immediate(ShowFind), Shortcut: $"{PrimaryLabel}F"),
            new("Toggle Line Comment", Action: Immediate(ToggleComment), Shortcut: $"{PrimaryLabel}/"),
            new("Configure Model Provider", Action: EditProviderAsync),
            new("Suggest Code at Cursor", Action: SuggestAsync, Shortcut: $"{PrimaryLabel}Alt+Space"),
            new("Focus Agent", Action: Immediate(FocusAgent), Shortcut: OperatingSystem.IsMacOS() ? "⌃⌘I" : "Ctrl+Alt+I")
        };
        var current = workspace;
        await using var progress = current is null ? null : new PickerIndex(current, lifetime.Token);
        var choice = await PickerWindow.PickWorkbenchAsync(this, actions, commands, progress is null ? null : progress.LoadAsync);
        if (choice?.Action is { } execute)
            await execute();
        else if (current is not null && choice?.FilePath is { } file)
            await OpenDocumentAsync(current.Resolve(file));
    }

    private async void OnQuickOpen(object? sender, RoutedEventArgs e) => await TryAsync(() => ShowPickerAsync(false));
    private async void OnCommandPalette(object? sender, RoutedEventArgs e) => await TryAsync(() => ShowPickerAsync(true));

    private sealed class PickerIndex(Workspace workspace, CancellationToken lifetimeToken) : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        private Task<string[]>? task;

        public Task<string[]> LoadAsync() => task ??= Task.Run(() => workspace.FilesAsync(cancel.Token), cancel.Token);

        public async ValueTask DisposeAsync()
        {
            cancel.Cancel();
            if (task is not null)
            {
                try { await task; }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException) { }
            }
            cancel.Dispose();
        }
    }
}
