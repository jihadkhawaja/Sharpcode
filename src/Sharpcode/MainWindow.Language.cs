using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Indentation.CSharp;
using AvaloniaEdit.TextMate;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow
{
    private Dictionary<string, EditorLanguage>? languageCatalog;
    private EditorLanguage activeLanguage = EditorLanguage.PlainText;
    private bool formattingDocument;

    private Dictionary<string, EditorLanguage> Languages => languageCatalog ??= Grammars.GetAvailableLanguages()
        .GroupBy(language => language.Id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => new EditorLanguage(group.Key, group.First().Aliases[0]), StringComparer.Ordinal);

    private void UpdateEditorLanguage(EditorTab? tab)
    {
        var id = tab?.LanguageOverride ?? (tab is null ? EditorLanguage.PlainText.Id : EditorLanguage.Detect(tab.Path,
            tab.Document.GetText(0, Math.Min(1_024, tab.Document.TextLength)), Grammars));
        activeLanguage = id == EditorLanguage.PlainText.Id ? EditorLanguage.PlainText
            : Languages.GetValueOrDefault(id) ?? new EditorLanguage(id, id);
        LanguageButton.IsVisible = tab is not null;
        LanguageButton.Content = activeLanguage.Name;
        ToolTip.SetTip(LanguageButton, tab?.LanguageOverride is null ? "Change file language · Auto detected" : "Change file language · Manual override");
        Editor.TextArea.IndentationStrategy = activeLanguage.IsCSharp
            ? new CSharpIndentationStrategy(Editor.Options) : new DefaultIndentationStrategy();

        var scope = tab is not null && tab.Document.TextLength <= DocumentFormatting.SizeLimit && tab.Document.LineCount <= 100_000
            ? Grammars.GetScopeByLanguageId(activeLanguage.Id) : null;
        if (syntaxScope != scope)
        {
            syntaxScope = scope;
            if (scope is null)
            {
                syntax?.Dispose();
                syntax = null;
            }
            else
            {
                syntax ??= Editor.InstallTextMate(Grammars, exceptionHandler: exception =>
                    Dispatcher.UIThread.Post(() => Status($"Syntax highlighting: {exception.Message}")));
                syntax.SetGrammar(scope);
            }
        }

        UpdateMarkdownSurface();
        QueueCSharpSyntaxCheck();
    }

    private async Task ChooseLanguageAsync()
    {
        if (activeTab is not { } tab)
            return;
        var choices = Languages.Values.Where(language => language.Id != EditorLanguage.PlainText.Id)
            .Append(EditorLanguage.PlainText).OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
            .Select(language => new PickerItem(language.Name, language.Id, FilePath: language.Id))
            .Prepend(new PickerItem("Auto Detect", "Use the file name, extension, or contents")).ToArray();
        var selection = await PickerWindow.PickAsync(this, "Change File Language", "Find a language", choices);
        if (selection is null || activeTab != tab)
            return;
        tab.LanguageOverride = selection.FilePath;
        UpdateEditorLanguage(tab);
        if (Editor.IsEffectivelyVisible)
            Editor.TextArea.Focus();
    }

    private async Task FormatDocumentAsync()
    {
        if (formattingDocument || activeTab is not { } tab || Editor.IsReadOnly)
            return;
        var language = activeLanguage;
        if (!DocumentFormatting.Supports(language.Id))
        {
            Status($"No built-in formatter for {language.Name}.");
            return;
        }
        if (tab.Document.TextLength > DocumentFormatting.SizeLimit)
        {
            Status("This document exceeds the formatting size limit.");
            return;
        }

        var snapshot = tab.Document.CreateSnapshot();
        var indent = Editor.Options.IndentationSize;
        var tabs = !Editor.Options.ConvertTabsToSpaces;
        var script = IsCSharpScript(tab);
        var token = lifetime.Token;
        formattingDocument = true;
        try
        {
            var edits = await Task.Run(() => DocumentFormatting.Format(language.Id, snapshot.Text, indent, tabs, script, token), token);
            if (activeTab != tab || language != activeLanguage || Editor.IsReadOnly || snapshot.Version.CompareAge(tab.Document.Version) != 0)
            {
                Status("The document changed while formatting. Run Format Document again.");
                return;
            }
            if (edits.Count == 0)
            {
                Status("Document is already formatted.");
                return;
            }
            using (tab.Document.RunUpdate())
                foreach (var edit in edits.OrderByDescending(edit => edit.Span.Start))
                    tab.Document.Replace(edit.Span.Start, edit.Span.Length, edit.NewText ?? "");
            Status($"Formatted {tab.Title}");
        }
        finally { formattingDocument = false; }
    }

    private static bool IsCSharpScript(EditorTab tab) => Path.GetExtension(tab.Path)?.Equals(".csx", StringComparison.OrdinalIgnoreCase) == true;

    private async void OnChooseLanguage(object? sender, RoutedEventArgs e) => await TryAsync(ChooseLanguageAsync);
    private async void OnFormatDocument(object? sender, RoutedEventArgs e) => await TryAsync(FormatDocumentAsync);
}
