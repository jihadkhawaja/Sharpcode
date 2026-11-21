using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Sharpcode.Core;

namespace Sharpcode;

public partial class App : Application
{
    private AppearanceSettings appearance = AppearanceSettings.Default;
    private (WorkbenchTheme Theme, string? Accent, bool Light)? appliedPalette;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ActualThemeVariantChanged += (_, _) => UpdatePalette();
        UpdatePalette();
    }

    internal void ApplyAppearance(AppearanceSettings settings)
    {
        appearance = settings;
        RequestedThemeVariant = settings.Theme switch
        {
            WorkbenchTheme.Light => ThemeVariant.Light,
            WorkbenchTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        UpdatePalette();
    }

    private void UpdatePalette()
    {
        var light = ActualThemeVariant == ThemeVariant.Light;
        var key = (appearance.Theme, appearance.Accent, light);
        if (appliedPalette == key)
            return;
        appliedPalette = key;
        WorkbenchPalette.Apply(this, appearance, light);
    }

    internal static IBrush Brush(string name) => Current is { } app &&
        app.TryGetResource(name, app.ActualThemeVariant, out var brush) && brush is IBrush color ? color : Brushes.Transparent;

    internal void SetFluentAccent(Color accent)
    {
        if (Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent &&
            fluent.Palettes.TryGetValue(ActualThemeVariant, out var palette))
            palette.Accent = accent;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault());
        base.OnFrameworkInitializationCompleted();
    }
}
