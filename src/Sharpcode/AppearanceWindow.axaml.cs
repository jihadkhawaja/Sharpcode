using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sharpcode.Core;

namespace Sharpcode;

internal sealed record AppearanceEdit(AppearanceSettings Settings, double FontSize, BackgroundAsset? Background);

internal partial class AppearanceWindow : Window
{
    private static readonly string?[] Presets = [null, "#A8F5CB", "#5CB3FF", "#B9ACFF", "#F4BD6A"];
    private static readonly FilePickerFileType ImageFiles = new("Images")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
    };

    private readonly Action<AppearanceEdit> preview;
    private readonly BackgroundAsset? originalBackground;
    private readonly CancellationTokenSource lifetime = new();
    private bool ready;
    private bool pickingImage;
    private bool closed;

    internal BackgroundAsset? SelectedBackground { get; private set; }

    internal AppearanceWindow(AppearanceEdit initial, Action<AppearanceEdit> preview)
    {
        this.preview = preview;
        originalBackground = initial.Background;
        SelectedBackground = initial.Background;
        InitializeComponent();
        LoadFields(initial.Settings, initial.FontSize);
        UpdatePreview();

        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
                Close(null);
        };
        Opened += (_, _) => ThemePicker.Focus();
    }

    private void LoadFields(AppearanceSettings settings, double fontSize)
    {
        ready = false;
        ThemePicker.SelectedIndex = (int)settings.Theme;
        var preset = Array.IndexOf(Presets, settings.Accent);
        AccentPicker.SelectedIndex = preset < 0 ? Presets.Length : preset;
        AccentHex.Text = settings.Accent ?? "#A8F5CB";
        EditorSize.Value = (decimal)fontSize;
        BackgroundOpacity.Value = settings.BackgroundOpacity;
        ImageSizing.SelectedIndex = (int)settings.BackgroundSizing;
        ready = true;
    }

    private (AppearanceEdit Edit, bool Valid) ReadFields()
    {
        var preset = Math.Clamp(AccentPicker.SelectedIndex, 0, Presets.Length);
        var accent = preset == Presets.Length ? AppearanceSettings.NormalizeAccent(AccentHex.Text) : Presets[preset];
        var settings = new AppearanceSettings((WorkbenchTheme)ThemePicker.SelectedIndex, accent,
            SelectedBackground?.Id, SelectedBackground?.Name, (int)Math.Round(BackgroundOpacity.Value),
            (EditorBackgroundSizing)ImageSizing.SelectedIndex).Normalize();
        return (new(settings, (double)(EditorSize.Value ?? 14), SelectedBackground), preset < Presets.Length || accent is not null);
    }

    private void UpdatePreview()
    {
        if (!ready || closed)
            return;
        var (edit, valid) = ReadFields();
        CustomAccentRow.IsVisible = AccentPicker.SelectedIndex == Presets.Length;
        AccentError.IsVisible = !valid;
        SaveAppearanceButton.IsEnabled = !pickingImage && valid;
        ChooseImageButton.IsEnabled = !pickingImage;
        RemoveImageButton.IsEnabled = !pickingImage && SelectedBackground is not null;
        ImageOptions.IsEnabled = SelectedBackground is not null;
        BackgroundName.Text = SelectedBackground?.Name ?? "No image selected";
        OpacityLabel.Text = $"{BackgroundOpacity.Value:F0}%";
        PreviewCode.FontSize = (double)(EditorSize.Value ?? 14);
        PreviewImage.Source = SelectedBackground?.Bitmap;
        PreviewImage.IsVisible = SelectedBackground is not null && BackgroundOpacity.Value > 0;
        PreviewImage.Opacity = BackgroundOpacity.Value / 100;
        PreviewImage.Stretch = ImageSizing.SelectedIndex == (int)EditorBackgroundSizing.Fit ? Stretch.Uniform : Stretch.UniformToFill;
        preview(edit);
    }

    private void SetBackground(BackgroundAsset? background)
    {
        var previous = SelectedBackground;
        SelectedBackground = background;
        ImageStatus.IsVisible = false;
        UpdatePreview();
        if (previous != originalBackground && previous != background)
            previous?.Dispose();
    }

    private async void OnChooseImage(object? sender, RoutedEventArgs e)
    {
        if (pickingImage)
            return;
        pickingImage = true;
        UpdatePreview();
        try
        {
            var token = lifetime.Token;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose editor background image",
                FileTypeFilter = [ImageFiles]
            });
            if (closed || files.FirstOrDefault() is not { } file)
                return;
            ImageStatus.Text = "Loading image…";
            ImageStatus.IsVisible = true;
            await using var stream = await file.OpenReadAsync();
            var image = await BackgroundAsset.ImportAsync(stream, file.Name, token);
            if (closed)
                image.Dispose();
            else
                SetBackground(image);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!closed)
            {
                ImageStatus.Text = exception.Message;
                ImageStatus.IsVisible = true;
            }
        }
        finally
        {
            pickingImage = false;
            UpdatePreview();
        }
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void OnAccentTextChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();
    private void OnOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e) => UpdatePreview();
    private void OnFontSizeChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdatePreview();
    private void OnRemoveImage(object? sender, RoutedEventArgs e) => SetBackground(null);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var (edit, valid) = ReadFields();
        if (!pickingImage && valid)
            Close(edit);
    }
    private void OnReset(object? sender, RoutedEventArgs e)
    {
        LoadFields(AppearanceSettings.Default, 14);
        SetBackground(null);
    }
}
