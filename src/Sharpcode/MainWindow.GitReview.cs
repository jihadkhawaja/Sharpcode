using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Sharpcode;

public partial class MainWindow
{
    private GitChangeRow? reviewedGitRow;
    private CancellationTokenSource? gitReviewRead;

    private async Task ShowGitReviewAsync(GitChangeRow row)
    {
        if (gitOperation is not null || gitRepository is not { } repository)
            return;
        gitReviewRead?.Cancel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        gitReviewRead = cancel;
        cancel.CancelAfter(TimeSpan.FromSeconds(20));

        var previous = reviewedGitRow;
        var sameDiff = previous is not null && previous.Change.Path == row.Change.Path && previous.Staged == row.Staged;
        reviewedGitRow = row;
        var path = Path.GetFullPath(row.Change.Path, repository.Root);
        var kind = row.Staged ? "Staged" : "Working tree";
        GitReviewTitle.Text = row.FileName;
        GitReviewDetails.Text = $"{kind} · {row.Description}" + (TabFor(path)?.IsDirty == true ? " · Unsaved edits excluded" : "");
        ToolTip.SetTip(GitReviewDetails, GitReviewDetails.Text);
        GitReviewStageButton.Content = row.ActionLabel;
        GitReviewOpenButton.IsVisible = File.Exists(path);
        GitReviewActions.IsEnabled = true;
        if (!sameDiff)
            GitDiffEditor.Text = "Loading diff…";
        GitReviewPane.IsVisible = true;
        EditorPane.IsVisible = WelcomePane.IsVisible = false;

        var activeList = row.Staged ? GitStaged : GitWorking;
        var otherList = row.Staged ? GitWorking : GitStaged;
        activeList.SelectedItem = activeList.Items.Contains(row) ? row : null;
        otherList.SelectedItem = null;
        try
        {
            var diff = await repository.DiffAsync(row.Change, row.Staged, cancel.Token);
            if (gitReviewRead != cancel || cancel.IsCancellationRequested)
                return;
            var text = diff.Length == 0 ? "No textual changes to display." : diff;
            if (GitDiffEditor.Text != text)
            {
                var offset = sameDiff ? GitDiffEditor.VerticalOffset : 0;
                GitDiffEditor.Text = text;
                GitDiffEditor.ScrollToVerticalOffset(offset);
            }
        }
        catch (OperationCanceledException)
        {
            if (gitReviewRead == cancel && gitOperation is null && !lifetime.IsCancellationRequested)
                GitDiffEditor.Text = "Reading the diff timed out. Refresh source control to retry.";
        }
        catch (IOException exception)
        {
            if (gitReviewRead == cancel && !cancel.IsCancellationRequested)
                GitDiffEditor.Text = exception.Message;
        }
        finally
        {
            if (gitReviewRead == cancel)
                gitReviewRead = null;
        }
    }

    private Task RefreshGitReviewAsync()
    {
        if (reviewedGitRow is not { } previous)
            return Task.CompletedTask;

        // Prefer the same side when a file has both staged and unstaged edits.
        var first = previous.Staged ? gitStagedRows : gitWorkingRows;
        var second = previous.Staged ? gitWorkingRows : gitStagedRows;
        var row = first.FirstOrDefault(MatchesPath) ?? second.FirstOrDefault(MatchesPath);
        if (row is not null)
            return ShowGitReviewAsync(row);
        CloseGitReview();
        return Task.CompletedTask;

        bool MatchesPath(GitChangeRow row) => row.Change.Path == previous.Change.Path;
    }

    private void CloseGitReview()
    {
        if (!GitReviewPane.IsVisible)
            return;
        gitReviewRead?.Cancel();
        gitReviewRead = null;
        reviewedGitRow = null;
        GitReviewPane.IsVisible = false;
        GitDiffEditor.Text = "";
        EditorPane.IsVisible = activeTab is not null;
        WelcomePane.IsVisible = activeTab is null;
    }

    private void DismissGitReview()
    {
        var list = reviewedGitRow?.Staged == true ? GitStaged : GitWorking;
        CloseGitReview();
        if (list.IsEffectivelyVisible)
            list.Focus();
        else if (activeTab is not null)
            Editor.TextArea.Focus();
    }

    private async void OnGitDiffTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is GitChangeRow row)
            await TryAsync(() => ShowGitReviewAsync(row));
    }

    private async void OnGitDiffKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is ListBox { SelectedItem: GitChangeRow row } && e.Source is not Button)
            await RunOnEnterAsync(e, () => ShowGitReviewAsync(row));
    }

    private void OnGitReviewClose(object? sender, RoutedEventArgs e) => DismissGitReview();

    private async void OnGitReviewStage(object? sender, RoutedEventArgs e)
    {
        if (reviewedGitRow is { } row)
            await StageGitAsync(row.Change, stage: !row.Staged);
    }

    private async void OnGitReviewOpenFile(object? sender, RoutedEventArgs e)
    {
        if (reviewedGitRow is { } row && gitRepository is { } repository)
            await TryAsync(() => OpenDocumentAsync(Path.GetFullPath(row.Change.Path, repository.Root)));
    }
}
