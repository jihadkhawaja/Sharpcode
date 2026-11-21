using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Sharpcode.Core;

namespace Sharpcode;

internal static class Dialogs
{
    public static Window Window(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = Math.Min(width, 440),
        MinHeight = Math.Min(height, 170),
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false
    };

    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 21, FontWeight = FontWeight.SemiBold };

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(13, 7) };
        if (primary)
            button.Classes.Add("primary");
        return button;
    }

    public static StackPanel Actions() => new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8
    };

    public static Task<string?> ChooseAsync(Window owner, string title, string explanation, params string[] choices)
    {
        var window = Window(title, 480, 200);
        window.CanResize = false;
        var actionBar = Actions();
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 13 };
        content.Children.Add(Heading(title));
        content.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(actionBar);
        window.Content = content;
        foreach (var choice in choices)
        {
            var button = Button(choice, choice == "Save");
            button.Click += (_, _) => window.Close(choice);
            actionBar.Children.Add(button);
        }
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) window.Close(null); };
        return window.ShowDialog<string?>(owner);
    }

    public static Task<string?> InputAsync(Window owner, string title, string label, string? initial = null)
    {
        var window = Window(title, 470, 210);
        window.CanResize = false;
        var input = new TextBox { Text = initial, PlaceholderText = label };
        Automation.SetName(input, label);
        var actions = Actions();
        var accept = Button("Continue", true);
        var cancel = Button("Cancel");
        accept.Click += (_, _) => window.Close(input.Text);
        cancel.Click += (_, _) => window.Close(null);
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children = { Heading(title), new TextBlock { Text = label }, input, actions }
        };
        window.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape))
                return;
            e.Handled = true;
            window.Close(e.Key == Key.Enter ? input.Text : null);
        }, RoutingStrategies.Tunnel);
        return window.ShowDialog<string?>(owner);
    }
}

internal static class Automation
{
    public static void SetName(Control control, string name) =>
        Avalonia.Automation.AutomationProperties.SetName(control, name);
}

internal sealed class PickerWindow : Window
{
    private readonly TextBox filter;
    private readonly ListBox results;
    private readonly TextBlock heading;
    private readonly TextBlock status = new();
    private IReadOnlyList<PickerItem> entries = [];
    private Func<Task<string[]>>? files;
    private PickerItem[]? fileEntries;
    private Task? loadingFiles;
    private string? fileError;
    private CancellationTokenSource? filtering;
    private bool closed;

    private PickerWindow(string title, string placeholder)
    {
        Title = title;
        Width = 700;
        Height = 460;
        MinWidth = 400;
        MinHeight = 240;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        heading = Dialogs.Heading(title);
        filter = new TextBox { Name = "PickerFilter", PlaceholderText = placeholder, Margin = new Thickness(0, 8, 0, 0) };
        Automation.SetName(filter, placeholder);
        results = new ListBox
        {
            Name = "PickerResults",
            Classes = { "picker" },
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PickerItem>((item, _) =>
                new Grid
                {
                    ColumnDefinitions = new("*,Auto"),
                    ColumnSpacing = 10,
                    Margin = new Thickness(2, 3),
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock { Text = item?.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                                new TextBlock { Text = item?.Detail, IsVisible = !string.IsNullOrEmpty(item?.Detail),
                                    TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Classes = { "muted" } }
                            }
                        },
                        InColumn(new Border
                        {
                            Classes = { "shortcut" },
                            IsVisible = !string.IsNullOrEmpty(item?.Shortcut),
                            Child = new TextBlock { Text = item?.Shortcut }
                        }, 1)
                    }
                })
        };
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(13), RowSpacing = 5 };
        grid.Children.Add(heading);
        grid.Children.Add(InRow(filter, 1));
        grid.Children.Add(InRow(results, 2));
        grid.Children.Add(InRow(status, 3));
        Content = grid;
        status.Classes.Add("muted");
        Opened += (_, _) => filter.Focus();
        filter.TextChanged += async (_, _) => await FilterAsync();
        results.DoubleTapped += (_, _) => Accept();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Closed += (_, _) => { closed = true; filtering?.Cancel(); };
    }

    public static Task<PickerItem?> PickAsync(Window owner, string title, string placeholder, IReadOnlyList<PickerItem> actions)
    {
        var window = new PickerWindow(title, placeholder) { entries = actions };
        window.ShowMatches(actions);
        return window.ShowDialog<PickerItem?>(owner);
    }

    public static Task<PickerItem?> PickWorkbenchAsync(Window owner, PickerItem[] commands, bool startInCommands,
        Func<Task<string[]>>? loadFiles)
    {
        var window = new PickerWindow("Quick Open", "Type a file name or > to run a command")
        {
            entries = commands,
            files = loadFiles
        };
        if (startInCommands)
            window.filter.Text = ">";
        window.Opened += async (_, _) =>
        {
            window.filter.CaretIndex = window.filter.Text?.Length ?? 0;
            await window.FilterAsync();
        };
        return window.ShowDialog<PickerItem?>(owner);
    }

    private async Task LoadFilesAsync()
    {
        try
        {
            var load = files!;
            fileEntries = await Task.Run(async () => (await load()).Select(path =>
            {
                var separator = path.LastIndexOf('/');
                return new PickerItem(path[(separator + 1)..], separator < 0 ? null : path[..separator], FilePath: path);
            }).ToArray());
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) { fileError = exception.Message; }
        if (!closed && !InCommandMode(filter.Text?.Trim() ?? ""))
            await FilterAsync();
    }

    private bool InCommandMode(string query) => files is null || query.StartsWith('>');

    private async Task FilterAsync()
    {
        filtering?.Cancel();
        using var cancel = new CancellationTokenSource();
        filtering = cancel;
        var query = filter.Text?.Trim() ?? "";
        var commandMode = InCommandMode(query);
        var items = commandMode ? entries : fileEntries;
        results.IsEnabled = false;
        if (files is not null)
            heading.Text = commandMode ? "Run a Command" : "Quick Open";
        if (query.StartsWith('>'))
            query = query[1..].TrimStart();
        try
        {
            if (items is null)
            {
                results.ItemsSource = null;
                status.Text = fileError ?? "Indexing workspace files…";
                if (fileError is null)
                    loadingFiles ??= LoadFilesAsync();
                return;
            }
            var matches = query.Length == 0 ? items.Take(150).ToArray()
                : await Task.Run(() => Match(items, query, cancel.Token), cancel.Token);
            if (!closed && filtering == cancel)
                ShowMatches(matches);
        }
        catch (OperationCanceledException) { }
        finally { if (filtering == cancel) filtering = null; }
    }

    private void ShowMatches(IReadOnlyList<PickerItem> matches)
    {
        results.ItemsSource = matches;
        results.SelectedIndex = matches.Count == 0 ? -1 : 0;
        results.IsEnabled = matches.Count > 0;
        status.Text = matches.Count == 0 ? "No matches. Try a different search." : "↑ ↓ Select    Enter Confirm    Esc Close";
    }

    private static PickerItem[] Match(IReadOnlyList<PickerItem> items, string query, CancellationToken cancellationToken)
    {
        var matches = new List<(PickerItem Item, int Score)>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = (item.FilePath ?? item.Detail ?? item.Label).AsSpan();
            var match = item.Label.AsSpan().IndexOf(query, StringComparison.OrdinalIgnoreCase);
            var score = match >= 0 ? match : 1000 + label.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (score == 999)
            {
                var next = 0;
                foreach (var character in label)
                {
                    if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[next]) && ++next == query.Length)
                        break;
                }
                if (next != query.Length)
                    continue;
                score = 3000 + label.Length;
            }
            matches.Add((item, score));
        }
        return matches.OrderBy(match => match.Score).Take(150).Select(match => match.Item).ToArray();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close(null);
                break;
            case Key.Enter:
                Accept();
                break;
            case Key.Up or Key.Down when results.Items.Count > 0:
                results.SelectedIndex = Math.Clamp(results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, results.Items.Count - 1);
                results.ScrollIntoView(results.SelectedItem!);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Accept()
    {
        if (results.IsEnabled && results.SelectedItem is PickerItem item)
            Close(item);
    }

    internal static T InColumn<T>(T control, int index) where T : Control { Grid.SetColumn(control, index); return control; }
    internal static T InRow<T>(T control, int index) where T : Control { Grid.SetRow(control, index); return control; }
}

internal static class DiffWindow
{
    public static Task ShowGitHistoryAsync(Window owner, string text)
    {
        const string title = "Recent Commits";
        var window = Dialogs.Window(title, 950, 650);
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(17), RowSpacing = 8 };
        grid.Children.Add(Dialogs.Heading(title));
        grid.Children.Add(PickerWindow.InRow(ReviewEditor(text), 1));

        var actions = Dialogs.Actions();
        var close = Dialogs.Button("Close");
        close.Click += (_, _) => window.Close();
        actions.Children.Add(close);
        grid.Children.Add(PickerWindow.InRow(actions, 2));
        window.Content = grid;
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) window.Close(); };
        return window.ShowDialog(owner);
    }

    public static void AddDiffHighlighting(TextEditor editor) => editor.TextArea.TextView.LineTransformers.Add(new DiffLineColors());

    public static async Task<string?> ReviewAsync(Window owner, ProposedChange change, CancellationToken cancellationToken)
    {
        var (diff, summary) = await Task.Run(() => Build(change), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var window = Dialogs.Window($"Review · {change.RelativePath}", 960, 680);
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(17), RowSpacing = 8 };
        grid.Children.Add(Dialogs.Heading(change.RelativePath));
        var description = new TextBlock { Text = summary, Classes = { "muted" } };
        grid.Children.Add(PickerWindow.InRow(description, 1));
        var diffEditor = ReviewEditor(diff);
        AddDiffHighlighting(diffEditor);
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Changes", Content = diffEditor },
                new TabItem { Header = "Original", Content = ReviewEditor(change.Original.Text) },
                new TabItem { Header = "Proposed", Content = ReviewEditor(change.Text) }
            }
        };
        grid.Children.Add(PickerWindow.InRow(tabs, 2));
        var actions = Dialogs.Actions();
        foreach (var label in new[] { "Close", "Discard Draft", "Apply File" })
        {
            var button = Dialogs.Button(label, label == "Apply File");
            button.Click += (_, _) => window.Close(label);
            actions.Children.Add(button);
        }
        grid.Children.Add(PickerWindow.InRow(actions, 3));
        window.Content = grid;
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) window.Close(null); };
        return await window.ShowDialog<string?>(owner);
    }

    private static (string Diff, string Summary) Build(ProposedChange change)
    {
        if (change.IsNew)
            return (string.Join('\n', change.Text.ReplaceLineEndings("\n").Split('\n').Select(line => "+ " + line)), "New file");
        if (change.Original.Text.Length + change.Text.Length > 320_000 ||
            change.Original.Text.Count(character => character == '\n') + change.Text.Count(character => character == '\n') > 6000)
            return ("Open the Original and Proposed tabs to inspect this large change.", "Large file · full text available in both tabs");

        var lines = InlineDiffBuilder.Diff(change.Original.Text, change.Text, false).Lines;
        var show = new bool[lines.Count];
        var added = 0;
        var removed = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Type == ChangeType.Unchanged)
                continue;
            added += lines[index].Type == ChangeType.Inserted ? 1 : 0;
            removed += lines[index].Type == ChangeType.Deleted ? 1 : 0;
            for (var line = Math.Max(0, index - 3); line <= Math.Min(lines.Count - 1, index + 3); line++)
                show[line] = true;
        }

        var output = new System.Text.StringBuilder();
        var gap = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!show[index])
            {
                gap = true;
                continue;
            }
            if (gap)
                output.AppendLine("  …");
            gap = false;
            var prefix = lines[index].Type switch { ChangeType.Inserted => "+ ", ChangeType.Deleted => "- ", _ => "  " };
            output.Append(prefix).AppendLine(lines[index].Text);
        }
        return (output.ToString(), $"+{added:N0} added   −{removed:N0} removed");
    }

    private static TextEditor ReviewEditor(string text) => new()
    {
        Document = new TextDocument(text),
        IsReadOnly = true,
        ShowLineNumbers = true,
        FontSize = 13,
        FontFamily = new("Cascadia Code, Menlo, DejaVu Sans Mono, monospace"),
        Background = App.Brush("Canvas"),
        Foreground = App.Brush("Ink"),
        LineNumbersForeground = App.Brush("Quiet"),
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    private sealed class DiffLineColors : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0)
                return;
            var document = CurrentContext.Document;
            var marker = document.GetCharAt(line.Offset);
            var color = marker switch
            {
                '+' when StartsWith("+++ ") => App.Brush("Muted"),
                '-' when StartsWith("--- ") => App.Brush("Muted"),
                'd' when StartsWith("diff --") => App.Brush("Muted"),
                'i' when StartsWith("index ") => App.Brush("Muted"),
                '+' => App.Brush("DiffAdded"),
                '-' => App.Brush("Danger"),
                '@' => App.Brush("Lavender"),
                _ => null
            };
            if (color is not null)
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(color));

            bool StartsWith(string prefix) => line.Length >= prefix.Length && document.GetText(line.Offset, prefix.Length) == prefix;
        }
    }
}
