using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Media;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow
{
    private BackgroundAsset? editorBackground;
    private Task? loadingBackground;
    private bool appearanceDialogOpen;

    private AppearanceSettings CurrentAppearance => (preferences.Appearance ?? AppearanceSettings.Default).Normalize();

    private void ApplyAppearance(AppearanceEdit edit)
    {
        if (Application.Current is App app)
            app.ApplyAppearance(edit.Settings);
        Editor.FontSize = Math.Clamp(edit.FontSize, 9, 32);
        WorkspaceBackground.Source = edit.Background?.Bitmap;
        WorkspaceBackground.IsVisible = edit.Background is not null && edit.Settings.BackgroundOpacity > 0;
        WorkspaceBackground.Opacity = edit.Settings.BackgroundOpacity / 100d;
        WorkspaceBackground.Stretch = edit.Settings.BackgroundSizing == EditorBackgroundSizing.Fit ? Stretch.Uniform : Stretch.UniformToFill;
    }

    private async Task LoadBackgroundAsync()
    {
        if (CurrentAppearance.BackgroundId is null)
            return;
        var image = await BackgroundAsset.OpenSavedAsync(CurrentAppearance, lifetime.Token);
        if (lifetime.IsCancellationRequested)
        {
            image?.Dispose();
            return;
        }
        editorBackground = image;
        ApplyAppearance(new(CurrentAppearance, preferences.FontSize, editorBackground));
    }

    private async Task EditAppearanceAsync()
    {
        if (loadingBackground is { } load)
            await load;
        if (appearanceDialogOpen || lifetime.IsCancellationRequested)
            return;
        var previousAppearance = preferences.Appearance;
        var previousFont = preferences.FontSize;
        var previousBackground = editorBackground;
        var dialog = new AppearanceWindow(new(CurrentAppearance, Editor.FontSize, previousBackground), ApplyAppearance);
        appearanceDialogOpen = true;
        try
        {
            var edit = await dialog.ShowDialog<AppearanceEdit?>(this);
            if (edit is null)
                return;
            if (edit.Background is { } image)
                await image.SaveAsync(lifetime.Token);
            preferences = preferences with { Appearance = edit.Settings, FontSize = edit.FontSize };
            try { await PersistPreferencesAsync(); }
            catch
            {
                preferences = preferences with { Appearance = previousAppearance, FontSize = previousFont };
                throw;
            }
            editorBackground = edit.Background;
            Status("Appearance saved");
        }
        finally
        {
            appearanceDialogOpen = false;
            if (!lifetime.IsCancellationRequested)
                ApplyAppearance(new(CurrentAppearance, preferences.FontSize, editorBackground));
            if (dialog.SelectedBackground != editorBackground && dialog.SelectedBackground != previousBackground)
                dialog.SelectedBackground?.Dispose();
            if (previousBackground != editorBackground)
                previousBackground?.Dispose();
        }
    }

    private async void OnAppearance(object? sender, RoutedEventArgs e) => await TryAsync(EditAppearanceAsync);
}
