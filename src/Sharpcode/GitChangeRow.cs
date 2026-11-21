using Avalonia.Media.Imaging;
using Sharpcode.Core;

namespace Sharpcode;

internal sealed record GitChangeRow(GitChange Change, bool Staged)
{
    public string FileName => Change.Path[(Change.Path.LastIndexOf('/') + 1)..];
    public string Directory => HasDirectory ? Change.Path[..Change.Path.LastIndexOf('/')] : "";
    public bool HasDirectory => Change.Path.Contains('/');
    public Bitmap Icon => MaterialExplorerIcons.For(FileName, directory: false);
    public char Code => Change.IsConflict ? '!' : Change.IsUntracked ? 'U' : Staged ? Change.Index : Change.Worktree;
    public bool IsAdded => Code is 'A' or 'U';
    public bool NeedsAttention => Code is '!' or 'D';
    public string ActionSymbol => Staged ? "−" : "+";
    public string ActionLabel => Staged ? "Unstage file" : Change.IsConflict ? "Stage resolved file" : "Stage file";

    public string State => Code switch
    {
        '!' => "Merge conflict",
        'A' => "Added",
        'C' => "Copied",
        'D' => "Deleted",
        'M' => "Modified",
        'R' => "Renamed",
        'T' => "File type changed",
        'U' => "Untracked",
        _ => "Changed"
    };

    public string DisplayPath => Change.OriginalPath is { } original && Staged ? $"{original} → {Change.Path}" : Change.Path;
    public string Description => $"{State} · {DisplayPath}";
}
