using System.ComponentModel;
using System.Text;
using AvaloniaEdit.Document;
using Sharpcode.Core;

namespace Sharpcode;

public sealed class EditorTab : INotifyPropertyChanged
{
    public EditorTab(string? path, string title, FileSnapshot file, TextDocument document)
    {
        Path = path;
        Title = title;
        Encoding = file.Encoding;
        Version = file.Version;
        Document = document;
        Document.UndoStack.SizeLimit = 1500;
        Document.UndoStack.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Document.UndoStack.IsOriginalFile))
                RefreshLabel();
        };
    }

    public string? Path { get; private set; }
    public string Title { get; private set; }
    public Encoding Encoding { get; private set; }
    public byte[] Version { get; private set; }
    public TextDocument Document { get; }
    public bool IsDirty => !Document.UndoStack.IsOriginalFile;
    public string Label => IsDirty ? $"{Title} •" : Title;
    public string FileType => System.IO.Path.GetExtension(Path)?.TrimStart('.') is { Length: > 0 } extension
        ? extension.ToUpperInvariant()[..Math.Min(4, extension.Length)]
        : "TXT";
    public int Caret { get; set; }
    public double VerticalOffset { get; set; }
    public bool IsSaving { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Saved(string path, byte[] version, bool isCurrentVersion)
    {
        Path = path;
        Title = System.IO.Path.GetFileName(path);
        Version = version;
        if (isCurrentVersion)
            Document.UndoStack.MarkAsOriginalFile();
        RefreshLabel();
        PropertyChanged?.Invoke(this, new(nameof(FileType)));
    }

    private void RefreshLabel() => PropertyChanged?.Invoke(this, new(nameof(Label)));

    public void Reload(FileSnapshot snapshot)
    {
        Document.Text = snapshot.Text;
        Encoding = snapshot.Encoding;
        Document.UndoStack.ClearAll();
        Saved(Path!, snapshot.Version, isCurrentVersion: true);
    }
}

public sealed class ChatEntry(string role, string text) : INotifyPropertyChanged
{
    public string Role { get; } = role;
    public string Text { get; private set; } = text;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string value)
    {
        Text = value;
        PropertyChanged?.Invoke(this, new(nameof(Text)));
    }
}

internal sealed record PickerItem(string Label, string? Detail = null, Func<Task>? Action = null, string? FilePath = null,
    string? Shortcut = null);

public sealed record RecentWorkspace(string Name, string Path);
