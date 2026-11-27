using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Sharpcode.Core;

namespace Sharpcode.Tests;

public sealed class EditorSafetyTests
{
    [Fact]
    public async Task EachDiskVersionAllowsAtMostOneConcurrentSave()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("sharpcode-save-");
        try
        {
            var path = Path.Combine(directory.FullName, "save.txt");
            await File.WriteAllTextAsync(path, new string('a', 2 * 1024 * 1024), token);
            var original = await TextFiles.ReadAsync(path, TextFiles.EditorLimit, token);
            var attempts = await Task.WhenAll(
                TryWriteAsync(path, "first"),
                TryWriteAsync(Path.Combine(directory.FullName, ".", "save.txt"), "second"));

            var written = Assert.Single(attempts.OfType<string>());
            Assert.Equal(written, await File.ReadAllTextAsync(path, token));
            Assert.Single(directory.GetFiles());

            async Task<string?> TryWriteAsync(string destination, string text)
            {
                try
                {
                    await TextFiles.WriteAsync(destination, text, original.Encoding, original.Version, token);
                    return text;
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void SavingAnOlderBufferInvalidatesThePreviousCleanUndoState()
    {
        var document = new TextDocument("previous disk text");
        var tab = new EditorTab("file.txt", "file.txt", new(document.Text, Encoding.UTF8, [1]), document);
        document.Insert(document.TextLength, " with edits");
        Assert.True(tab.IsDirty);
        var saving = document.CreateSnapshot();
        document.UndoStack.Undo();
        Assert.False(tab.IsDirty);

        tab.Saved("file.txt", [2], saving.Version.CompareAge(document.Version) == 0);

        Assert.True(tab.IsDirty);
        Assert.Equal("previous disk text", document.Text);
        Assert.Equal(new byte[] { 2 }, tab.Version);
    }
}

public sealed partial class WorkbenchTests
{
    [AvaloniaFact]
    public async Task ConflictingSaveKeepsTheEditorOpenAndReloadRequiresConsent()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(project.Path, "conflict.txt");
        await File.WriteAllTextAsync(path, "original", token);
        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        var tabs = window.FindControl<ListBox>("Tabs")!;

        await UntilAsync(() => editor.Text == "original", token);
        editor.Document.Insert(editor.Document.TextLength, " + unsaved");
        await File.WriteAllTextAsync(path, "external update", token);
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Press(window, Key.W, primary);
        await UntilAsync(() => window.OwnedWindows.Count > 0, token);
        Choose(window, "Save");
        var error = window.FindControl<TextBlock>("ErrorText")!;
        await UntilAsync(() => error.Text?.Contains("changed on disk", StringComparison.Ordinal) == true, token);
        Assert.True(Assert.IsType<EditorTab>(Assert.Single(tabs.Items)).IsDirty);
        Assert.Equal("original + unsaved", editor.Text);
        Assert.Equal("external update", await File.ReadAllTextAsync(path, token));

        var reload = window.FindControl<MenuItem>("ReloadMenuItem")!;
        reload.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.Count > 0, token);
        Choose(window, "Cancel");
        Assert.Equal("original + unsaved", editor.Text);
        await UntilAsync(() => window.OwnedWindows.Count == 0, token);

        reload.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => window.OwnedWindows.Count > 0, token);
        Choose(window, "Reload");
        await UntilAsync(() => editor.Text == "external update", token);
        Assert.False(Assert.IsType<EditorTab>(Assert.Single(tabs.Items)).IsDirty);
        editor.Document.Insert(editor.Document.TextLength, " + reconciled");
        window.FindControl<MenuItem>("SaveMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => tabs.SelectedItem is EditorTab { IsSaving: false, IsDirty: false }, token);
        Assert.Equal("external update + reconciled", await File.ReadAllTextAsync(path, token));

        window.Close();
        await UntilAsync(() => !window.IsVisible, token);
    }

    [AvaloniaFact]
    public async Task WindowClosePreservesAnEditMadeAfterOpeningTheDiscardPrompt()
    {
        using var data = new TestDirectory();
        using var project = new TestDirectory();
        using var settings = new SettingsOverride(Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR"));
        Environment.SetEnvironmentVariable("SHARPCODE_DATA_DIR", data.Path);
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(project.Path, "close.txt");
        await File.WriteAllTextAsync(path, "original", token);
        var window = new MainWindow(path);
        window.Show();
        var editor = window.FindControl<TextEditor>("Editor")!;
        await UntilAsync(() => editor.Text == "original", token);
        editor.Document.Insert(editor.Document.TextLength, " + first edit");

        window.Close();
        await UntilAsync(() => window.OwnedWindows.Count > 0, token);
        editor.Document.Insert(editor.Document.TextLength, " + later edit");
        Choose(window, "Discard");
        var status = window.FindControl<TextBlock>("StatusText")!;
        await UntilAsync(() => status.Text?.Contains("changed while closing", StringComparison.Ordinal) == true, token);
        Assert.True(window.IsVisible);
        Assert.Equal("original + first edit + later edit", editor.Text);

        window.Close();
        await UntilAsync(() => window.OwnedWindows.Count > 0, token);
        Choose(window, "Discard");
        await UntilAsync(() => !window.IsVisible, token);
        Assert.Equal("original", await File.ReadAllTextAsync(path, token));
    }

    private static void Choose(Window owner, string choice)
    {
        var dialog = Assert.Single(owner.OwnedWindows);
        var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), control => Equals(control.Content, choice));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
