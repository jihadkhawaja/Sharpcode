using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sharpcode.Core;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

[assembly: AvaloniaTestApplication(typeof(Sharpcode.Tests.TestAppBuilder))]

namespace Sharpcode.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions()).WithInterFont();
}

public sealed class WorkbenchTests
{
    [AvaloniaFact]
    public async Task RightClickOpensEditorActionsAndPreservesTheSelection()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        const string source = "class Sample { }\n";
        var path = Path.Combine(project.Path, "sample.cs");
        await File.WriteAllTextAsync(path, source, token);

        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        try
        {
            await UntilAsync(() => editor.Text == source && editor.TextArea.TextView.Bounds.Width > 0, token);
            editor.Select(0, "class Sample".Length);
            var menu = Assert.IsType<ContextMenu>(editor.TextArea.ContextMenu);
            ClickAt(window, editor.TextArea.TextView, new Point(15, 8), MouseButton.Right);
            await UntilAsync(() => menu.IsOpen, token);
            Assert.Equal("class Sample", editor.SelectedText);
            Assert.True(MenuItemNamed(menu, "Copy").IsEnabled);
            Assert.True(MenuItemNamed(menu, "Ask Agent About This Selection").IsEnabled);
            MenuItemNamed(menu, "Toggle Line Comment").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("// " + source, editor.Text);
            menu.Close();

            editor.IsReadOnly = true;
            ClickAt(window, editor.TextArea.TextView, new Point(17, 8), MouseButton.Right);
            await UntilAsync(() => menu.IsOpen, token);
            Assert.False(MenuItemNamed(menu, "Paste").IsEnabled);
            Assert.False(MenuItemNamed(menu, "Toggle Line Comment").IsEnabled);
            Assert.False(MenuItemNamed(menu, "Undo").IsEnabled);
            Assert.True(MenuItemNamed(menu, "Find in File").IsEnabled);
        }
        finally
        {
            editor.TextArea.ContextMenu?.Close();
            editor.Document.UndoStack.MarkAsOriginalFile();
            window.Close();
            await UntilAsync(() => !window.IsVisible, token);
        }
    }

    [AvaloniaFact]
    public async Task ExplorerFolderTogglesFromItsLabelPaddingAndChevron()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var folderPath = Directory.CreateDirectory(Path.Combine(project.Path, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folderPath, "entry.cs"), "// Workspace fixture", token);

        var window = new MainWindow(project.Path);
        window.Show();
        var tree = window.FindControl<TreeView>("FileTree")!;
        await UntilAsync(() => tree.Items.Count == 1, token);
        var folder = Assert.IsAssignableFrom<TreeViewItem>(tree.Items[0]);
        var folderHeader = Assert.IsType<StackPanel>(folder.Header);
        var label = folderHeader.Children.OfType<TextBlock>().Single();
        await UntilAsync(() => label.Bounds.Width > 0, token);

        ClickAt(window, label, new Point(5, label.Bounds.Height / 2));
        await UntilAsync(() => folder.IsExpanded && folder.Items.OfType<TreeViewItem>().Any(item => item.Tag is WorkspaceEntry), token);

        var row = folder.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "PART_LayoutRoot" && border.TemplatedParent == folder);
        ClickAt(window, row, new Point(row.Bounds.Width - 6, row.Bounds.Height / 2));
        Assert.False(folder.IsExpanded);

        var chevron = folder.GetVisualDescendants().OfType<ToggleButton>()
            .Single(button => button.Name == "PART_ExpandCollapseChevron" && button.TemplatedParent == folder);
        ClickAt(window, chevron, new Point(chevron.Bounds.Width / 2, chevron.Bounds.Height / 2));
        Assert.True(folder.IsExpanded);
        tree.SelectedItem = folder;
        tree.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.False(folder.IsExpanded);
        ClickAt(window, label, new Point(5, label.Bounds.Height / 2));
        var file = Assert.IsAssignableFrom<TreeViewItem>(Assert.Single(folder.Items));
        var fileHeader = Assert.IsType<StackPanel>(file.Header);
        var fileLabel = fileHeader.Children.OfType<TextBlock>().Single();
        Assert.Same(MaterialExplorerIcons.For("src", directory: true), folderHeader.Children.OfType<Image>().Single().Source);
        Assert.Same(MaterialExplorerIcons.For("entry.cs", directory: false), fileHeader.Children.OfType<Image>().Single().Source);
        Assert.NotSame(fileHeader.Children.OfType<Image>().Single().Source, folderHeader.Children.OfType<Image>().Single().Source);
        await UntilAsync(() => folder.IsExpanded && fileLabel.Bounds.Width > 0, token);
        ClickAt(window, fileLabel, new Point(5, fileLabel.Bounds.Height / 2));
        await UntilAsync(() => window.FindControl<TextEditor>("Editor")!.Text == "// Workspace fixture", token);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task WorkspaceSearchExplainsEmptyResultsAndOpensTheClickedMatch()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var sourceFolder = Directory.CreateDirectory(Path.Combine(project.Path, "src"));
        const string source = "First line\nA searchable phrase\n";
        await File.WriteAllTextAsync(Path.Combine(sourceFolder.FullName, "notes.txt"), source, token);

        var window = new MainWindow(project.Path);
        window.Show();
        try
        {
            await UntilAsync(() => window.FindControl<Button>("WelcomeBrowse")!.IsVisible, token);
            window.FindControl<Button>("SearchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var summary = window.FindControl<TextBlock>("SearchSummary")!;
            Assert.Contains(Path.GetFileName(project.Path), summary.Text);
            var input = window.FindControl<TextBox>("SearchInput")!;
            input.Text = "missing from the project";
            await UntilAsync(() => summary.Text?.StartsWith("No matches.", StringComparison.Ordinal) == true, token);

            input.Text = "searchable phrase";
            var results = window.FindControl<ListBox>("SearchResults")!;
            await UntilAsync(() => results.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.Text == "notes.txt:2" && block.Bounds.Width > 0), token);
            Assert.Equal("1 matching line", summary.Text);
            var match = results.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == "notes.txt:2");
            ClickAt(window, match, new Point(5, match.Bounds.Height / 2));
            var editor = window.FindControl<TextEditor>("Editor")!;
            await UntilAsync(() => editor.Text == source && editor.TextArea.Caret.Line == 2, token);
            Assert.Equal(3, editor.TextArea.Caret.Column);
        }
        finally
        {
            window.Close();
            await UntilAsync(() => !window.IsVisible, token);
        }
    }

    [AvaloniaFact]
    public async Task SourceControlPanelReviewsStagesAndCommitsAFile()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var repo = await GitRepository.InitAsync(project.Path, token);
        await GitRepositoryTests.ConfigureIdentityAsync(project.Path, token);
        var src = Directory.CreateDirectory(Path.Combine(project.Path, "src"));
        var path = Path.Combine(src.FullName, "one.cs");
        const string text = "class Sample { }\n";
        await File.WriteAllTextAsync(path, text, token);
        await File.WriteAllTextAsync(Path.Combine(project.Path, "README.md"), "# Sample\n", token);
        var window = new MainWindow(path);
        window.Show();
        Press(window, Key.G, KeyModifiers.Control | KeyModifiers.Shift);
        var working = window.FindControl<ListBox>("GitWorking")!;
        var staged = window.FindControl<ListBox>("GitStaged")!;
        var details = window.FindControl<Grid>("GitDetails")!;
        var editor = window.FindControl<TextEditor>("Editor")!;
        await UntilAsync(() => working.Items.Count == 2 && details.IsEnabled && editor.Text == text, token);
        Assert.Equal("2", window.FindControl<TextBlock>("GitBadgeCount")!.Text);
        var filter = window.FindControl<TextBox>("GitFilter")!;
        filter.Text = "no matches";
        await UntilAsync(() => working.Items.Count == 0, token);
        Assert.True(window.FindControl<TextBlock>("GitChangesEmpty")!.IsVisible);
        filter.Text = "src/";
        await UntilAsync(() => working.Items.Count == 1 && working.GetVisualDescendants()
            .OfType<TextBlock>().Any(block => block.Text == "one.cs" && block.Bounds.Width > 0), token);
        Assert.Contains("1/2", window.FindControl<ToggleButton>("GitWorkingTitle")!.Content?.ToString());

        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var review = window.FindControl<Border>("GitReviewPane")!;
        var diff = window.FindControl<TextEditor>("GitDiffEditor")!;
        var file = working.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "one.cs");
        ClickAt(window, file, new Point(5, file.Bounds.Height / 2));
        await UntilAsync(() => review.IsVisible && diff.Text.Contains("+class Sample { }", StringComparison.Ordinal), token);
        Assert.Empty(window.OwnedWindows);
        Assert.True(diff.IsReadOnly);
        Press(window, Key.W, primary);
        Assert.False(review.IsVisible);
        Assert.True(editor.IsEffectivelyVisible);
        Assert.Equal(text, editor.Text);

        working.Focus();
        Press(working, Key.Enter);
        await UntilAsync(() => review.IsVisible && diff.Text.Contains("+class Sample { }", StringComparison.Ordinal), token);
        var stageButton = window.FindControl<Button>("GitReviewStageButton")!;
        stageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => staged.Items.Count == 1 && working.Items.Count == 0 && details.IsEnabled &&
            Equals(stageButton.Content, "Unstage file") && diff.Text.Contains("+class Sample { }", StringComparison.Ordinal), token);
        Assert.StartsWith("Staged", window.FindControl<TextBlock>("GitReviewDetails")!.Text);
        var message = window.FindControl<TextBox>("GitCommitMessage")!;
        var commit = window.FindControl<Button>("GitCommitButton")!;
        Assert.False(commit.IsEnabled);
        message.Text = "Add sample";
        await UntilAsync(() => commit.IsEnabled, token);
        Assert.Equal("Commit (1)", commit.Content);
        message.Focus();
        Press(message, Key.Enter, primary);
        await UntilAsync(() => staged.Items.Count == 0 && details.IsEnabled && !review.IsVisible, token);
        Assert.Equal("1", window.FindControl<TextBlock>("GitBadgeCount")!.Text);
        Assert.Contains("Add sample", await repo.HistoryAsync(token));
        Assert.Equal("README.md", Assert.Single((await repo.StatusAsync(token)).Changes).Path);

        window.FindControl<Button>("ExplorerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.FindControl<Grid>("GitPane")!.IsVisible);
        editor.Document.Insert(editor.Document.TextLength, "// Edit from the Explorer pane\n");
        Press(window, Key.S, primary);
        await UntilAsync(() => window.FindControl<TextBlock>("GitBadgeCount")!.Text == "2", token);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task SourceControlOffersPublishFetchPullAndPushAsTrackingChanges()
    {
        using var data = new TestDirectory();
        using var fixture = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var path = Directory.CreateDirectory(Path.Combine(fixture.Path, "work")).FullName;
        var remote = Directory.CreateDirectory(Path.Combine(fixture.Path, "remote.git")).FullName;
        var clone = Path.Combine(fixture.Path, "clone");
        await GitRepositoryTests.RunGitAsync(remote, token, "init", "--bare", "--initial-branch=main");
        var repo = await GitRepository.InitAsync(path, token);
        await GitRepositoryTests.ConfigureIdentityAsync(path, token);
        var file = Path.Combine(path, "notes.txt");
        await File.WriteAllTextAsync(file, "First\n", token);
        await repo.StageAsync(null, token);
        await repo.CommitAsync("First commit", token);

        var window = new MainWindow(file);
        window.Show();
        Press(window, Key.G, KeyModifiers.Control | KeyModifiers.Shift);
        var editor = window.FindControl<TextEditor>("Editor")!;
        var sync = window.FindControl<Button>("GitSyncButton")!;
        var refresh = window.FindControl<Button>("GitRefreshButton")!;
        await UntilAsync(() => Equals(sync.Content, "Add Remote…") && editor.Text == "First\n", token);
        await repo.AddRemoteAsync("origin", remote, token);
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Equals(sync.Content, "Publish Branch") && sync.IsEffectivelyEnabled, token);
        sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Equals(sync.Content, "Fetch…") && sync.IsEffectivelyEnabled, token);
        Assert.Equal("origin/main", (await repo.StatusAsync(token)).Upstream);

        await GitRepository.CloneAsync(remote, clone, token);
        var peer = Assert.IsType<GitRepository>(await GitRepository.DiscoverAsync(clone, token));
        await GitRepositoryTests.ConfigureIdentityAsync(clone, token);
        await File.WriteAllTextAsync(Path.Combine(clone, "notes.txt"), "Second\n", token);
        await peer.StageAsync(null, token);
        await peer.CommitAsync("Second commit", token);
        await peer.PushAsync(await peer.StatusAsync(token), remote: null, token);
        var footerFetch = window.FindControl<Button>("StatusFetchButton")!;
        Assert.True(footerFetch.IsEnabled);
        footerFetch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Equals(sync.Content, "Pull ↓1") && sync.IsEffectivelyEnabled, token);
        sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Equals(sync.Content, "Fetch…") && editor.Text == "Second\n", token);

        await File.WriteAllTextAsync(file, "Third\n", token);
        await repo.StageAsync(null, token);
        await repo.CommitAsync("Third commit", token);
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Equals(sync.Content, "Push ↑1") && sync.IsEffectivelyEnabled, token);
        sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Equals(sync.Content, "Fetch…") && sync.IsEffectivelyEnabled, token);
        await peer.FetchAsync("origin", token);
        Assert.Equal(1, (await peer.StatusAsync(token)).Behind);
        await GitRepositoryTests.RunGitAsync(path, token, "branch", "feature/status-bar");
        window.FindControl<Button>("StatusBranchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.Count == 1, token);
        var branches = window.OwnedWindows.Single();
        branches.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PickerFilter").Text = "feature/status-bar";
        await UntilAsync(() => PickerSelection(branches)?.Label == "feature/status-bar", token);
        Press(branches, Key.Enter);
        await UntilAsync(() => window.FindControl<TextBlock>("BranchLabel")!.Text == "feature/status-bar", token);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task MarkdownPreviewUsesTheBufferAndMiddleClickClosesOnlyTheClickedTab()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(project.Path, "README.md");
        const string markdown = "# Notebook\n\n**Readable** text with `inline code`.\n\n| Editor | Platform |\n| --- | --- |\n| Sharpcode | Desktop |\n\n```cs\nint value = 42;\n```\n";
        await File.WriteAllTextAsync(path, markdown, token);
        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        var language = window.FindControl<Button>("LanguageButton")!;
        await UntilAsync(() => editor.Text == markdown && Equals(language.Content, "Markdown"), token);
        editor.Document.Insert(2, "Unsaved ");
        var previewButton = window.FindControl<Button>("MarkdownPreviewButton")!;
        previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var preview = window.FindControl<StackPanel>("MarkdownPreviewContent")!;
        await UntilAsync(() => preview.Children.OfType<SelectableTextBlock>().Any(block =>
            block.Inlines?.OfType<Run>().Any(run => run.Text == "Unsaved Notebook") == true) &&
            preview.GetVisualDescendants().OfType<SelectableTextBlock>().Any(block => block.Text == "int value = 42;"), token);
        Assert.False(editor.IsVisible);
        Assert.Equal(markdown, await File.ReadAllTextAsync(path, token));
        previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(editor.IsVisible);
        Assert.Contains("Unsaved Notebook", editor.Text);

        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Press(window, Key.N, primary);
        var tabs = window.FindControl<ListBox>("Tabs")!;
        await UntilAsync(() => tabs.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "README.md" && block.Bounds.Width > 0), token);
        var previous = tabs.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == "README.md");
        var previousTab = previous.FindAncestorOfType<ListBoxItem>()!;
        Assert.Contains(previousTab.GetVisualDescendants().OfType<Ellipse>(), indicator =>
            indicator.Classes.Contains("unsavedIndicator") && indicator.IsEffectivelyVisible);
        ClickAt(window, previous, new Point(5, previous.Bounds.Height / 2), MouseButton.Middle);
        await UntilAsync(() => window.OwnedWindows.Count == 1, token);
        Assert.StartsWith("Untitled", Assert.IsType<EditorTab>(tabs.SelectedItem).Title);
        Assert.Equal(2, tabs.Items.Count);
        Press(window.OwnedWindows.Single(), Key.Escape);
        await UntilAsync(() => window.OwnedWindows.Count == 0, token);
        Assert.Equal(2, tabs.Items.Count);
        ClickAt(window, previous, new Point(5, previous.Bounds.Height / 2), MouseButton.Middle);
        await UntilAsync(() => window.OwnedWindows.Count == 1, token);
        var discard = window.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Discard"));
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => tabs.Items.Count == 1, token);
        Assert.StartsWith("Untitled", Assert.IsType<EditorTab>(tabs.SelectedItem).Title);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task CSharpProblemsShowLiveSyntaxAndProjectDiagnosticsAndFormattingIsUndoable()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(project.Path, "Notebook.cs");
        const string source = "class Notebook{\nint Value=>\"bad\";\nvoid Note(){int unused;}\n}\n";
        await File.WriteAllTextAsync(path, source, token);
        await File.WriteAllTextAsync(Path.Combine(project.Path, "Notebook.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", token);
        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        var language = window.FindControl<Button>("LanguageButton")!;
        await UntilAsync(() => editor.Text == source && Equals(language.Content, "C#"), token);
        var problems = window.FindControl<ListBox>("ProblemList")!;
        editor.Document.Insert(0, "class Incomplete {\n");
        await UntilAsync(() => problems.Items.OfType<ProblemRow>().Any(row => row.IsLive && row.IsError), token);
        editor.Undo();
        await UntilAsync(() => problems.Items.Count == 0, token);

        Press(window, Key.F, KeyModifiers.Shift | KeyModifiers.Alt);
        await UntilAsync(() => editor.Text.Contains("int Value => \"bad\";", StringComparison.Ordinal), token);
        Assert.NotEqual(source, editor.Text);
        editor.Undo();
        Assert.Equal(source, editor.Text);
        window.FindControl<Button>("StatusProblemsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FindControl<Button>("CheckCSharpProjectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => problems.Items.OfType<ProblemRow>().Any(row => !row.IsLive && row.Code == "CS0029"), token,
            TimeSpan.FromSeconds(35));
        var error = Assert.Single(problems.Items.OfType<ProblemRow>(), row => row.Code == "CS0029");
        Assert.True(error.IsError);
        Assert.Equal(path, error.Problem.Path);
        Assert.Contains(problems.Items.OfType<ProblemRow>(), row => !row.IsError && row.Code == "CS0168");
        problems.SelectedItem = error;
        Press(problems, Key.Enter);
        await UntilAsync(() => editor.TextArea.Caret.Line == 2, token);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    private static MenuItem MenuItemNamed(ContextMenu menu, string label) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label));

    private static void ClickAt(Window window, Visual target, Point position, MouseButton button = MouseButton.Left)
    {
        var point = target.TranslatePoint(position, window) ?? throw new InvalidOperationException("The control is detached.");
        window.MouseDown(point, button, RawInputModifiers.None);
        window.MouseUp(point, button, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task OpensEditsAndSavesAFileThroughTheWorkbench()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        var oldDataDirectory = Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR");
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var path = System.IO.Path.Combine(project.Path, "sample.cs");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "class Sample { }\n", token);

        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        try
        {
            await UntilAsync(() => editor.Text == "class Sample { }\n", token);
            Assert.True(window.FindControl<Grid>("EditorPane")!.IsVisible);
            Assert.Single(window.FindControl<ListBox>("Tabs")!.Items);
            editor.Document.Insert(editor.Document.TextLength, "// Test");
            Assert.False(editor.Document.UndoStack.IsOriginalFile);

            window.FindControl<MenuItem>("SaveMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UntilAsync(() => File.ReadAllText(path).EndsWith("// Test", StringComparison.Ordinal), token);
            await UntilAsync(() => editor.Document.UndoStack.IsOriginalFile, token);
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.N,
                KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control
            });
            Assert.Equal(2, window.FindControl<ListBox>("Tabs")!.Items.Count);
        }
        finally
        {
            editor.Document.UndoStack.MarkAsOriginalFile();
            window.Close();
            await UntilAsync(() => !window.IsVisible, token);
            Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", oldDataDirectory);
        }
    }

    [AvaloniaFact]
    public async Task ReviewsAndAppliesAChangeProposedByAnOpenAiCompatibleEndpoint()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        var oldDataDirectory = Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR");
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        using var settings = new SettingsOverride(oldDataDirectory);
        var token = TestContext.Current.CancellationToken;
        await using var server = await StartProviderAsync(token);
        await new Preferences(new(ModelProvider.LMStudio, "fixture", $"{server.Urls.Single()}/v1")).SaveAsync();
        var path = System.IO.Path.Combine(project.Path, "greeting.txt");
        await File.WriteAllTextAsync(path, "hello\n", token);
        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        await UntilAsync(() => editor.Text == "hello\n", token);

        var mode = window.FindControl<ComboBox>("AgentModePicker")!;
        var prompt = window.FindControl<TextBox>("PromptBox")!;
        var notice = window.FindControl<SelectableTextBlock>("AgentNotice")!;
        Assert.Equal(0, mode.SelectedIndex);
        Assert.False(window.FindControl<Button>("SendButton")!.IsEnabled);

        prompt.Text = "Test provider failure";
        window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => notice.Classes.Contains("error"), token);
        Assert.Contains("HTTP 400", notice.Text);
        Assert.Equal("Test provider failure", prompt.Text);

        mode.SelectedIndex = 1;
        prompt.Text = "Update the greeting.";
        window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var drafts = window.FindControl<ListBox>("ReviewFiles")!;
        await UntilAsync(() => drafts.Items.Count == 1, token);
        Assert.Equal("hello\n", await File.ReadAllTextAsync(path, token));
        var draft = Assert.IsType<ProposedChange>(drafts.Items[0]);
        Assert.Equal("welcome\n", draft.Text);
        drafts.SelectedIndex = 0;
        window.FindControl<Button>("ReviewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.Count > 0, token);
        var review = Assert.Single(window.OwnedWindows);
        var apply = review.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Apply File"));
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => drafts.Items.Count == 0 && editor.Text == "welcome\n", token);
        Assert.Equal("welcome\n", await File.ReadAllTextAsync(path, token));
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task QuickOpenFindsFilesAndSwitchesToCommands()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var script = Path.Combine(project.Path, "startup.cs");
        await File.WriteAllTextAsync(script, "// Workspace fixture", token);
        var window = new MainWindow(project.Path);
        window.Show();
        await UntilAsync(() => window.FindControl<Button>("WelcomeBrowse")!.IsVisible, token);
        Assert.False(window.FindControl<Grid>("AgentPane")!.IsVisible);

        async Task<Window> OpenPickerAsync(string query)
        {
            Press(window, Key.P, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
            await UntilAsync(() => window.OwnedWindows.Count == 1, token);
            var picker = window.OwnedWindows.Single();
            var filter = picker.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PickerFilter");
            filter.Text = query;
            return picker;
        }

        var files = await OpenPickerAsync("startup");
        await UntilAsync(() => PickerSelection(files)?.FilePath == "startup.cs", token);
        Press(files, Key.Enter);
        var editor = window.FindControl<TextEditor>("Editor")!;
        await UntilAsync(() => editor.Text == "// Workspace fixture", token);

        var commands = await OpenPickerAsync("> new file");
        await UntilAsync(() => PickerSelection(commands)?.Label == "New File", token);
        Press(commands, Key.Enter);
        await UntilAsync(() => window.FindControl<ListBox>("Tabs")!.Items.Count == 2, token);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task RecentWorkspaceCanBeReopenedAndCompactLayoutKeepsTheEditorUsable()
    {
        using var data = new TestDirectory();
        using var first = new TestDirectory();
        using var second = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        await new Preferences(ModelSettings.Default, RecentWorkspaces: [first.Path]).SaveAsync();
        var window = new MainWindow(second.Path) { Width = 980 };
        window.Show();
        var recent = window.FindControl<ItemsControl>("RecentList")!;
        await UntilAsync(() => recent.Items.OfType<RecentWorkspace>().Any(folder => folder.Path == first.Path), token);
        Dispatcher.UIThread.RunJobs();
        var sidebar = window.FindControl<Grid>("Sidebar")!;
        var agent = window.FindControl<Grid>("AgentPane")!;
        Assert.True(sidebar.IsVisible);
        Assert.False(agent.IsVisible);
        window.FindControl<Button>("AgentToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(sidebar.IsVisible);
        Assert.True(agent.IsVisible);
        window.Width = 1300;
        await UntilAsync(() => sidebar.IsVisible && agent.IsVisible, token);

        var openRecent = recent.GetVisualDescendants().OfType<Button>()
            .Single(button => button.DataContext is RecentWorkspace folder && folder.Path == first.Path);
        openRecent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Preferences.Load().LastWorkspace == first.Path, token);
        Assert.Equal([first.Path, second.Path], Assert.IsType<string[]>(Preferences.Load().RecentWorkspaces));
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task AppearancePreviewsAndCancelsThenPersistsThemeAccentAndFont()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(project.Path, "sample.cs");
        await File.WriteAllTextAsync(path, "public record Note(string Text);", token);

        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        await UntilAsync(() => editor.Text.Length > 0 && editor.TextArea.TextView.Bounds.Width > 0, token);
        var menu = window.FindControl<MenuItem>("AppearanceMenuItem")!;
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.Count == 1, token);
        var dialog = window.OwnedWindows.Single();
        dialog.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = (int)WorkbenchTheme.Light;
        dialog.FindControl<NumericUpDown>("EditorSize")!.Value = 21;
        Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
        Assert.Equal(21, editor.FontSize);
        Assert.Equal(Color.Parse("#FAFBFD"), Assert.IsType<SolidColorBrush>(App.Brush("Canvas")).Color);
        Press(dialog, Key.Escape);
        await UntilAsync(() => window.OwnedWindows.Count == 0 && window.ActualThemeVariant == ThemeVariant.Dark, token);
        Assert.Equal(14, editor.FontSize);
        Assert.Null(Preferences.Load().Appearance);

        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Press(window, Key.OemComma, primary);
        await UntilAsync(() => window.OwnedWindows.Count == 1, token);
        dialog = window.OwnedWindows.Single();
        dialog.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = (int)WorkbenchTheme.Midnight;
        dialog.FindControl<ComboBox>("AccentPicker")!.SelectedIndex = 5;
        var hex = dialog.FindControl<TextBox>("AccentHex")!;
        var save = dialog.FindControl<Button>("SaveAppearanceButton")!;
        hex.Text = "#abc";
        await UntilAsync(() => !save.IsEnabled, token);
        hex.Text = "#c07edd";
        await UntilAsync(() => save.IsEnabled, token);
        dialog.FindControl<NumericUpDown>("EditorSize")!.Value = 18;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Preferences.Load().Appearance?.Accent == "#C07EDD", token);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);

        var reopened = new MainWindow();
        reopened.Show();
        Assert.Equal(WorkbenchTheme.Midnight, Preferences.Load().Appearance?.Theme);
        Assert.Equal(Color.Parse("#0C1425"), Assert.IsType<SolidColorBrush>(App.Brush("Canvas")).Color);
        Assert.Equal(Color.Parse("#C07EDD"), Assert.IsType<SolidColorBrush>(App.Brush("Accent")).Color);
        Assert.Equal(18, reopened.FindControl<TextEditor>("Editor")!.FontSize);
        reopened.Close();
        await UntilAsync(() => !reopened.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task ManagedBackgroundLoadsAndRemoveCanBeCanceledOrSaved()
    {
        using var data = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        await using var stream = AssetLoader.Open(new Uri("avares://Sharpcode/Assets/Sharpcode.png"));
        using var image = await BackgroundAsset.ImportAsync(stream, "Sharpcode.png", token);
        await image.SaveAsync(token);
        await new Preferences(ModelSettings.Default, Appearance: new(BackgroundId: image.Id, BackgroundName: image.Name,
            BackgroundOpacity: 30, BackgroundSizing: EditorBackgroundSizing.Fit)).SaveAsync();

        var window = new MainWindow();
        window.Show();
        var background = window.FindControl<Image>("WorkspaceBackground")!;
        await UntilAsync(() => background.IsVisible, token);
        Assert.NotNull(background.Source);
        Assert.Equal(0.30, background.Opacity);
        Assert.Equal(Stretch.Uniform, background.Stretch);
        var menu = window.FindControl<MenuItem>("AppearanceMenuItem")!;
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.Count == 1, token);
        var dialog = window.OwnedWindows.Single();
        dialog.FindControl<Button>("RemoveImageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(background.IsVisible);
        Press(dialog, Key.Escape);
        await UntilAsync(() => window.OwnedWindows.Count == 0 && background.IsVisible, token);
        Assert.Equal(image.Id, Preferences.Load().Appearance?.BackgroundId);

        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.Count == 1, token);
        dialog = window.OwnedWindows.Single();
        dialog.FindControl<Button>("RemoveImageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.FindControl<Button>("SaveAppearanceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Preferences.Load().Appearance?.BackgroundId is null, token);
        Assert.Null(background.Source);
        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    private static PickerItem? PickerSelection(Window picker) => picker.GetVisualDescendants().OfType<ListBox>()
        .Single(list => list.Name == "PickerResults").SelectedItem as PickerItem;

    private static void Press(InputElement element, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        element.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static async Task<WebApplication> StartProviderAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        server.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var messages = body.RootElement.GetProperty("messages");
            var latest = messages[messages.GetArrayLength() - 1];
            if (latest.GetProperty("content").GetString() == "Test provider failure")
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Fixture rejected this prompt.", context.RequestAborted);
                return;
            }
            var hasResult = latest.GetProperty("role").GetString() == "tool";
            var delta = hasResult ? (object)new { content = "I prepared a draft." } : new
            {
                tool_calls = new[]
                {
                    new { index = 0, id = "draft", function = new { name = "replace_text",
                        arguments = """{"path":"greeting.txt","old_text":"hello","new_text":"welcome"}""" } }
                }
            };
            var payload = JsonSerializer.Serialize(new { choices = new[] { new { delta } } });
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync($"data: {payload}\n\ndata: [DONE]\n\n", context.RequestAborted);
        });
        await server.StartAsync(cancellationToken);
        return server;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(8));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("sharpcode-ui-");

        public string Path => directory.FullName;

        public void Dispose() => GitRepositoryTests.DeleteFixture(directory);
    }

    private sealed class SettingsOverride(string? original) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", original);
    }
}
