namespace Sharpcode.Core;

public enum WorkbenchTheme { Dark, Light, Midnight, System }
public enum EditorBackgroundSizing { Fill, Fit }

public sealed record AppearanceSettings(
    WorkbenchTheme Theme = WorkbenchTheme.Dark,
    string? Accent = null,
    string? BackgroundId = null,
    string? BackgroundName = null,
    int BackgroundOpacity = 18,
    EditorBackgroundSizing BackgroundSizing = EditorBackgroundSizing.Fill)
{
    public static AppearanceSettings Default { get; } = new();
    public const int MaxBackgroundOpacity = 45;

    public static string? NormalizeAccent(string? value)
    {
        var hex = value?.Trim();
        if (hex?.StartsWith('#') == true)
            hex = hex[1..];
        return hex is { Length: 6 } && hex.All(Uri.IsHexDigit) ? $"#{hex.ToUpperInvariant()}" : null;
    }

    public AppearanceSettings Normalize() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : WorkbenchTheme.Dark,
        Accent = NormalizeAccent(Accent),
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, MaxBackgroundOpacity),
        BackgroundSizing = Enum.IsDefined(BackgroundSizing) ? BackgroundSizing : EditorBackgroundSizing.Fill
    };
}
