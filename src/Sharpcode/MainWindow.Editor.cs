using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using Avalonia.VisualTree;
using Sharpcode.Core;
using TextMateSharp.Grammars;

namespace Sharpcode;

public partial class MainWindow
{
    private readonly ObservableCollection<EditorTab> openTabs = [];
    private readonly HashSet<string> opening = new(Workspace.PathComparer);
    private RegistryOptions? darkGrammars;
    private RegistryOptions? lightGrammars;
    private RegistryOptions Grammars => ActualThemeVariant == ThemeVariant.Light
        ? lightGrammars ??= new RegistryOptions(ThemeName.LightPlus)
        : darkGrammars ??= new RegistryOptions(ThemeName.DarkPlus);
    private TextMate.Installation? syntax;
    private string? syntaxScope;
    private EditorTab? activeTab;
    private int untitledCount;

    private void InitializeEditor()
    {
        Tabs.ItemsSource = openTabs;
        Tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Editor.Options.IndentationSize = 4;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.ShowBoxForControlCharacters = true;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateEditorStatus();
        Editor.TextChanged += (_, _) => OnEditorTextChanged();
        ActualThemeVariantChanged += (_, _) => syntax?.SetTheme(Grammars.GetDefaultTheme());
        InitializeEditorContextMenu();
        InitializeCodeProblems();
    }

    private async Task OpenFileAsync()
    {
        var items = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open files",
            AllowMultiple = true,
            SuggestedStartLocation = await WorkspaceLocationAsync()
        });
        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is { } path)
                await TryAsync(() => OpenDocumentAsync(path));
            else
                Status("Choose a file on the local filesystem.");
        }
    }

    private async Task<IStorageFolder?> WorkspaceLocationAsync() =>
        workspace is null ? null : await StorageProvider.TryGetFolderFromPathAsync(workspace.Root);

    private async Task OpenDocumentAsync(string path, int? line = null, int column = 1)
    {
        path = Path.GetFullPath(path);
        if (new FileInfo(path).LinkTarget is not null)
            path = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        var existing = TabFor(path);
        if (existing is not null)
        {
            ActivateTab(existing);
            if (line is { } target)
                JumpTo(target, column);
            return;
        }
        if (!opening.Add(path))
            return;

        var owner = Thread.CurrentThread;
        try
        {
            var (file, document) = await Task.Run(async () =>
            {
                var snapshot = await TextFiles.ReadAsync(path, TextFiles.EditorLimit, lifetime.Token);
                var parsed = new TextDocument(snapshot.Text);
                parsed.SetOwnerThread(owner);
                return (snapshot, parsed);
            }, lifetime.Token);
            var tab = new EditorTab(path, Path.GetFileName(path), file, document);
            openTabs.Add(tab);
            ActivateTab(tab);
            Status($"Opened {tab.Title}");
            if (line is { } target)
                JumpTo(target, column);
        }
        finally { opening.Remove(path); }
    }

    private EditorTab? TabFor(string path) => openTabs.FirstOrDefault(tab => Workspace.PathComparer.Equals(tab.Path, path));

    private void NewFile()
    {
        var tab = new EditorTab(null, $"Untitled-{++untitledCount}", FileSnapshot.New, new TextDocument());
        openTabs.Add(tab);
        ActivateTab(tab);
    }

    private void ActivateTab(EditorTab? tab)
    {
        CloseGitReview();
        if (tab == activeTab)
        {
            if (tab is not null && Editor.IsEffectivelyVisible)
                Editor.TextArea.Focus();
            return;
        }
        DismissSuggestion();
        if (activeTab is not null)
        {
            activeTab.Caret = Editor.CaretOffset;
            activeTab.VerticalOffset = Editor.VerticalOffset;
        }
        activeTab = tab;
        UpdateAttachment();
        EditorPane.IsVisible = tab is not null;
        WelcomePane.IsVisible = tab is null;
        if (Tabs.SelectedItem != tab)
            Tabs.SelectedItem = tab;
        if (tab is null)
        {
            Editor.Document = new TextDocument();
            UpdateEditorLanguage(null);
            EditorStatus.Text = "";
            return;
        }

        Editor.Document = tab.Document;
        UpdateEditorReadOnly();
        UpdateBreadcrumbAndSyntax(tab);
        Editor.CaretOffset = Math.Min(tab.Caret, tab.Document.TextLength);
        Editor.ScrollToVerticalOffset(tab.VerticalOffset);
        if (Editor.IsEffectivelyVisible)
            Editor.TextArea.Focus();
        UpdateEditorStatus();
    }

    private void UpdateEditorReadOnly() => Editor.IsReadOnly = (gitWorktreeUpdating && gitRepository is { } repository &&
        activeTab?.Path is { } path && IsUnderRepository(repository, path)) ||
        (applyingPath is not null && Workspace.PathComparer.Equals(activeTab?.Path, applyingPath));

    private void UpdateBreadcrumbAndSyntax(EditorTab tab)
    {
        UpdateAttachment();
        var displayPath = tab.Path is null ? tab.Title : workspace?.Relative(tab.Path) ?? tab.Path;
        Breadcrumb.Text = displayPath.Replace('/', '›').Replace('\\', '›').Replace("›", "  ›  ");
        ToolTip.SetTip(Breadcrumb, tab.Path ?? tab.Title);
        UpdateEditorLanguage(tab);
    }

    private void OnEditorTextChanged()
    {
        UpdateEditorStatus();
        if (activeTab is { LanguageOverride: null } tab && string.IsNullOrEmpty(Path.GetExtension(tab.Path)) &&
            tab.Document.TextLength <= 4_096 && EditorLanguage.Detect(tab.Path,
                tab.Document.GetText(0, Math.Min(1_024, tab.Document.TextLength)), Grammars) != activeLanguage.Id)
        {
            UpdateEditorLanguage(tab);
            return;
        }
        if (MarkdownPreviewPane.IsVisible)
            UpdateMarkdownSurface();
        QueueCSharpSyntaxCheck(documentChanged: true);
    }

    private void UpdateEditorStatus()
    {
        if (activeTab is not { } tab)
            return;
        var caret = Editor.TextArea.Caret;
        var selection = Editor.SelectionLength;
        var encoding = tab.Encoding.WebName.ToUpperInvariant();
        var selected = selection == 0 ? "" : $" · {selection:N0} selected";
        EditorStatus.Text = $"Ln {caret.Line:N0}, Col {caret.Column:N0}{selected}    {encoding}";
    }

    private async Task SaveActiveAsync(bool forcePicker = false)
    {
        if (activeTab is { } tab)
            await SaveTabAsync(tab, forcePicker);
    }

    private async Task<bool> SaveTabAsync(EditorTab tab, bool forcePicker = false)
    {
        if (gitWorktreeUpdating || tab.IsSaving || (applyingPath is not null && Workspace.PathComparer.Equals(tab.Path, applyingPath)))
            return false;
        tab.IsSaving = true;
        try
        {
            var destination = tab.Path;
            var expected = tab.Version;
            if (forcePicker || destination is null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save file",
                    SuggestedFileName = tab.Title,
                    SuggestedStartLocation = await WorkspaceLocationAsync(),
                    ShowOverwritePrompt = true
                });
                destination = file?.TryGetLocalPath();
                if (destination is null)
                    return false;
                if (TabFor(destination) is { } other && other != tab)
                    throw new IOException("That file is already open in another editor.");
                if (!Workspace.PathComparer.Equals(destination, tab.Path))
                    expected = File.Exists(destination)
                        ? (await TextFiles.ReadAsync(destination, TextFiles.EditorLimit, lifetime.Token)).Version : [];
            }

            if (File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Save As requires a regular file.");
            var snapshot = tab.Document.CreateSnapshot();
            var version = await Task.Run(() => TextFiles.WriteAsync(destination, snapshot.Text, tab.Encoding, expected, lifetime.Token), lifetime.Token);
            tab.Saved(destination, version, snapshot.Version.CompareAge(tab.Document.Version) == 0);
            if (tab == activeTab)
                UpdateBreadcrumbAndSyntax(tab);
            Status($"Saved {tab.Title}");
            if (expected.Length == 0)
                await RefreshExplorerAsync();
            ScheduleGitRefresh();
            return true;
        }
        finally { tab.IsSaving = false; }
    }

    private async Task SaveAllAsync()
    {
        foreach (var tab in openTabs.Where(tab => tab.IsDirty).ToArray())
        {
            if (!await SaveTabAsync(tab))
                break;
        }
    }

    private async Task CloseTabAsync(EditorTab? tab)
    {
        if (tab is null || tab.IsSaving || (applyingPath is not null && Workspace.PathComparer.Equals(tab.Path, applyingPath)))
            return;
        if (tab.IsDirty)
        {
            var decision = await UnsavedAsync(tab);
            if (decision is null or "Cancel" || (decision == "Save" && !await SaveTabAsync(tab)))
                return;
        }

        var index = openTabs.IndexOf(tab);
        if (index < 0)
            return;
        if (tab == activeTab)
            ActivateTab(openTabs.Count == 1 ? null : openTabs[index == openTabs.Count - 1 ? index - 1 : index + 1]);
        openTabs.Remove(tab);
    }

    private void CycleTabs(int direction)
    {
        if (openTabs.Count == 0)
            return;
        var current = Tabs.SelectedIndex;
        ActivateTab(openTabs[(current + direction + openTabs.Count) % openTabs.Count]);
    }

    private void ShowFind() => OpenFindPanel(false);
    private void ShowReplace() => OpenFindPanel(true);

    private void OpenFindPanel(bool replace)
    {
        if (activeTab is null || replace && Editor.IsReadOnly)
            return;
        ShowMarkdownSource();
        Editor.ApplyTemplate();
        var panel = Editor.SearchPanel;
        if (panel is null)
            return;
        var selection = Editor.SelectionLength is > 0 and <= 256 ? Editor.SelectedText : "";
        panel.IsReplaceMode = replace;
        panel.Open();
        if (selection.Length > 0 && !selection.Contains('\n'))
            panel.SearchPattern = selection;
        Dispatcher.UIThread.Post(panel.Reactivate, DispatcherPriority.Loaded);
    }

    private async Task GoToLineAsync()
    {
        if (activeTab is null)
            return;
        var result = await Dialogs.InputAsync(this, "Go to Line", $"Line number (1–{Editor.Document.LineCount:N0})");
        if (int.TryParse(result, out var line))
            JumpTo(line);
    }

    private void JumpTo(int line, int column = 1)
    {
        if (activeTab is not { } tab)
            return;
        ShowMarkdownSource();
        var target = tab.Document.GetLineByNumber(Math.Clamp(line, 1, tab.Document.LineCount));
        var position = Math.Clamp(column, 1, target.Length + 1);
        Editor.Select(target.Offset + position - 1, 0);
        Editor.CaretOffset = target.Offset + position - 1;
        Dispatcher.UIThread.Post(() =>
        {
            if (activeTab == tab)
                Editor.ScrollTo(target.LineNumber, position);
        }, DispatcherPriority.Loaded);
        Editor.TextArea.Focus();
    }

    private void ToggleComment()
    {
        if (activeTab is not { } tab || Editor.IsReadOnly)
            return;
        var prefix = LineCommentPrefix(activeLanguage.Id);
        if (prefix is null)
        {
            Status("Line comments are unavailable for this file type.");
            return;
        }
        var doc = tab.Document;
        var first = doc.GetLineByOffset(Editor.SelectionStart).LineNumber;
        var last = doc.GetLineByOffset(Editor.SelectionStart + Math.Max(0, Editor.SelectionLength - 1)).LineNumber;
        var lines = new List<(int Offset, string Content)>();
        for (var number = first; number <= last; number++)
        {
            var line = doc.GetLineByNumber(number);
            var text = doc.GetText(line.Offset, line.Length);
            var content = text.TrimStart();
            if (content.Length > 0 || first == last)
                lines.Add((line.Offset + text.Length - content.Length, content));
        }
        var remove = lines.All(line => line.Content.StartsWith(prefix, StringComparison.Ordinal));
        using (doc.RunUpdate())
        {
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                if (remove)
                    doc.Remove(line.Offset, prefix.Length + (line.Content.AsSpan(prefix.Length).StartsWith(" ") ? 1 : 0));
                else
                    doc.Insert(line.Offset, prefix + " ");
            }
        }
    }

    private static string? LineCommentPrefix(string language) => language switch
    {
        "python" or "powershell" or "shellscript" or "ruby" or "yaml" or "toml" or "r" => "#",
        "sql" or "lua" => "--",
        "csharp" or "fsharp" or "javascript" or "typescript" or "javascriptreact" or "typescriptreact" or "go" or "rust" or
            "cpp" or "c" or "java" or "kotlin" or "swift" or "dart" or "scala" or "zig" or "jsonc" => "//",
        _ => null
    };

    private void ChangeFontSize(int amount)
    {
        Editor.FontSize = amount == 0 ? 14 : Math.Clamp(Editor.FontSize + amount, 9, 32);
        preferences = preferences with { FontSize = Editor.FontSize };
    }

    private void OnNewFile(object? sender, RoutedEventArgs e) => NewFile();
    private async void OnOpenFile(object? sender, RoutedEventArgs e) => await TryAsync(OpenFileAsync);
    private async void OnSave(object? sender, RoutedEventArgs e) => await TryAsync(() => SaveActiveAsync());
    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await TryAsync(() => SaveActiveAsync(true));
    private async void OnSaveAll(object? sender, RoutedEventArgs e) => await TryAsync(SaveAllAsync);
    private async void OnCloseTab(object? sender, RoutedEventArgs e) => await TryAsync(() => CloseTabAsync(activeTab));
    private async void OnCloseSpecificTab(object? sender, RoutedEventArgs e) => await TryAsync(() => CloseTabAsync((sender as Control)?.DataContext as EditorTab));
    private async void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle || e.Source is not Visual source ||
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not EditorTab tab)
            return;
        e.Handled = true;
        await TryAsync(() => CloseTabAsync(tab));
    }
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e) { if (Tabs.SelectedItem is EditorTab tab) ActivateTab(tab); }
    private void OnUndo(object? sender, RoutedEventArgs e) { if (!Editor.IsReadOnly) Editor.Undo(); }
    private void OnRedo(object? sender, RoutedEventArgs e) { if (!Editor.IsReadOnly) Editor.Redo(); }
    private void OnFind(object? sender, RoutedEventArgs e) => ShowFind();
    private void OnToggleComment(object? sender, RoutedEventArgs e) => ToggleComment();
    private async void OnGoToLine(object? sender, RoutedEventArgs e) => await TryAsync(GoToLineAsync);
    private void OnZoomIn(object? sender, RoutedEventArgs e) => ChangeFontSize(1);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => ChangeFontSize(-1);
}
