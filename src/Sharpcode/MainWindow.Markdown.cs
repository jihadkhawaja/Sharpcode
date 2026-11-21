using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit.Document;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow
{
    private readonly List<Bitmap> markdownImages = [];
    private CancellationTokenSource? markdownLoad;
    private ITextSourceVersion? markdownVersion;
    private EditorTab? markdownTab;

    private void ToggleMarkdownPreview()
    {
        if (activeTab is not { } tab || !activeLanguage.IsMarkdown)
            return;
        CloseGitReview();
        tab.ShowMarkdownPreview = !tab.ShowMarkdownPreview;
        if (tab.ShowMarkdownPreview)
            DismissSuggestion();
        UpdateMarkdownSurface();
        if (!tab.ShowMarkdownPreview)
            Editor.TextArea.Focus();
        else
            MarkdownPreviewPane.Focus();
    }

    private void ShowMarkdownSource()
    {
        if (activeTab is { ShowMarkdownPreview: true } tab)
        {
            tab.ShowMarkdownPreview = false;
            UpdateMarkdownSurface();
        }
    }

    private void UpdateMarkdownSurface()
    {
        var markdown = activeTab is not null && activeLanguage.IsMarkdown;
        var show = markdown && activeTab!.ShowMarkdownPreview;
        MarkdownPreviewButton.IsVisible = MarkdownPreviewMenu.IsEnabled = markdown;
        var label = show ? "Edit Markdown" : "Preview Markdown";
        MarkdownPreviewButton.Content = MarkdownPreviewMenu.Header = label;
        Editor.IsVisible = !show;
        MarkdownPreviewPane.IsVisible = show;
        if (show && activeTab is { } tab)
        {
            if (tab != markdownTab || markdownVersion?.CompareAge(tab.Document.Version) != 0)
                _ = TryAsync(() => LoadMarkdownPreviewAsync(tab));
        }
        else
            ClearMarkdownPreview();
    }

    private async Task LoadMarkdownPreviewAsync(EditorTab tab)
    {
        ClearMarkdownPreview();
        var version = tab.Document.Version;
        markdownTab = tab;
        markdownVersion = version;
        var limited = tab.Document.TextLength > MarkdownPreview.CharacterLimit;
        var text = tab.Document.GetText(0, Math.Min(tab.Document.TextLength, MarkdownPreview.CharacterLimit));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        markdownLoad = cancel;
        MarkdownPreviewPane.Offset = default;
        MarkdownPreviewContent.Children.Add(new TextBlock { Text = "Rendering preview…", Classes = { "muted" } });
        try
        {
            var parsed = await Task.Run(() => MarkdownPreview.Parse(text), cancel.Token);
            if (markdownLoad != cancel || cancel.IsCancellationRequested)
                return;
            MarkdownPreviewContent.Children.Clear();
            var images = new List<Task>();
            var renderer = new MarkdownPreview(url => TryAsync(() => OpenMarkdownLinkAsync(tab, url)), (url, alt) =>
            {
                if (images.Count >= 8 || ResolveMarkdownPath(tab, url) is not { } path || !File.Exists(path))
                    return null;
                var target = new Image { MaxHeight = 480, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                Avalonia.Automation.AutomationProperties.SetName(target, alt);
                var placeholder = new TextBlock { Text = alt, Classes = { "muted" } };
                var view = new StackPanel { Children = { placeholder, target } };
                images.Add(LoadMarkdownImageAsync(path, target, placeholder, cancel.Token));
                return view;
            });
            renderer.Render(MarkdownPreviewContent, parsed, limited);
            await Task.WhenAll(images);
        }
        finally
        {
            if (markdownLoad == cancel)
                markdownLoad = null;
        }
    }

    private async Task LoadMarkdownImageAsync(string path, Image target, TextBlock placeholder, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var bitmap = await BackgroundAsset.ReadImageAsync(stream, token);
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }
            markdownImages.Add(bitmap);
            target.Source = bitmap;
            placeholder.IsVisible = false;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void ClearMarkdownPreview()
    {
        if (markdownTab is null)
            return;
        markdownLoad?.Cancel();
        markdownLoad = null;
        markdownTab = null;
        markdownVersion = null;
        MarkdownPreviewContent.Children.Clear();
        foreach (var image in markdownImages)
            image.Dispose();
        markdownImages.Clear();
    }

    private string? ResolveMarkdownPath(EditorTab tab, string url)
    {
        if (tab.Path is not { } file || string.IsNullOrWhiteSpace(url) || Uri.TryCreate(url, UriKind.Absolute, out _) ||
            url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith('#'))
            return null;
        var directory = Path.GetDirectoryName(file)!;
        try
        {
            var relative = Uri.UnescapeDataString(url.Split(['#', '?'], 2)[0]);
            if (Path.IsPathRooted(relative))
                return null;
            var path = Path.GetFullPath(relative, directory);
            var current = workspace is { } opened && !IsOutside(opened.Relative(file)) ? opened : new Workspace(directory);
            return current.Resolve(current.Relative(path));
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return null; }

        static bool IsOutside(string relative) => Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal);
    }

    private async Task OpenMarkdownLinkAsync(EditorTab tab, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto")
            await Launcher.LaunchUriAsync(uri);
        else if (ResolveMarkdownPath(tab, url) is { } path && File.Exists(path))
            await OpenDocumentAsync(path);
        else
            Status("This Markdown link is unavailable.");
    }

    private void OnMarkdownPreview(object? sender, RoutedEventArgs e) => ToggleMarkdownPreview();
}
