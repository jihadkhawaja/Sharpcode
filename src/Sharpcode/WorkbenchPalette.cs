using Avalonia.Media;
using Sharpcode.Core;

namespace Sharpcode;

internal static class WorkbenchPalette
{
    private sealed record Colors(string Canvas, string Chrome, string Panel, string Raised, string Divider, string Ink,
        string Muted, string Quiet, string Accent, string Lavender, string Danger, string Added);

    public static void Apply(App app, AppearanceSettings settings, bool light)
    {
        var values = light
            ? new Colors("#FAFBFD", "#E9EDF4", "#F1F4F8", "#E1E7F0", "#CBD4E1", "#171F2E", "#48566C", "#5C6B82", "#14785C", "#5842A6", "#A0263A", "#156749")
            : settings.Theme == WorkbenchTheme.Midnight
                ? new Colors("#0C1425", "#0F192D", "#111E34", "#172944", "#2D415E", "#E8F0FF", "#ACBDD5", "#8EA1BE", "#8DD9FC", "#C4B7FF", "#FDA5AB", "#91E9BD")
                : new Colors("#101219", "#13161F", "#181C26", "#222836", "#333B4A", "#E7EBF3", "#ABB5C8", "#808DA3", "#A8F5CB", "#B9ACFF", "#FDA5AB", "#A8F5CB");

        var panel = Color.Parse(values.Panel);
        var canvas = Color.Parse(values.Canvas);
        var chrome = Color.Parse(values.Chrome);
        var ink = Color.Parse(values.Ink);
        var accent = Color.Parse(settings.Accent ?? values.Accent);
        var active = Mix(panel, accent, light ? .12 : .16);
        var activeHover = Mix(panel, accent, light ? .19 : .23);
        var accentInk = ReadableAccent(accent, ink, panel, chrome, canvas, active, activeHover);
        var accentHover = Mix(accent, light ? ColorsBlack : ColorsWhite, .11);
        var onAccent = Contrast(ColorsBlack, accent) >= Contrast(ColorsWhite, accent) ? ColorsBlack : ColorsWhite;
        var onAccentHover = Contrast(ColorsBlack, accentHover) >= Contrast(ColorsWhite, accentHover) ? ColorsBlack : ColorsWhite;

        void Set(string name, Color value) => app.Resources[name] = new SolidColorBrush(value);
        void SetHex(string name, string value) => Set(name, Color.Parse(value));
        Set("Canvas", canvas);
        Set("Chrome", chrome);
        Set("Panel", panel);
        SetHex("Raised", values.Raised);
        SetHex("Divider", values.Divider);
        Set("Ink", ink);
        SetHex("Muted", values.Muted);
        SetHex("Quiet", values.Quiet);
        Set("Accent", accent);
        Set("AccentInk", accentInk);
        Set("AccentSurface", active);
        Set("NavHover", activeHover);
        Set("AccentHover", accentHover);
        Set("OnAccent", onAccent);
        Set("OnAccentHover", onAccentHover);
        Set("TabSelection", Mix(Color.Parse(values.Raised), accent, light ? .14 : .24));
        Set("EditorSelection", Mix(panel, accent, light ? .28 : .31));
        Set("CurrentLine", Color.FromArgb(25, accentInk.R, accentInk.G, accentInk.B));
        app.Resources["CurrentLineBorder"] = new Pen(new SolidColorBrush(Color.FromArgb(65, accentInk.R, accentInk.G, accentInk.B)));
        SetHex("Lavender", values.Lavender);
        SetHex("Danger", values.Danger);
        SetHex("Warning", light ? "#845610" : "#F4BD6A");
        SetHex("DiffAdded", values.Added);
        app.SetFluentAccent(accentInk);
    }

    private static readonly Color ColorsBlack = Color.FromRgb(0, 0, 0);
    private static readonly Color ColorsWhite = Color.FromRgb(255, 255, 255);

    private static Color ReadableAccent(Color accent, Color ink, params Color[] backgrounds)
    {
        for (var step = 0; step <= 10; step++)
        {
            var mixed = Mix(accent, ink, step / 10d);
            if (backgrounds.All(background => Contrast(mixed, background) >= 4.5))
                return mixed;
        }
        return ink;
    }

    private static Color Mix(Color from, Color to, double fraction)
    {
        byte Blend(byte left, byte right) => (byte)Math.Round(left + (right - left) * fraction);
        return Color.FromRgb(Blend(from.R, to.R), Blend(from.G, to.G), Blend(from.B, to.B));
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        static double Luminance(Color value) => .2126 * Linear(value.R) + .7152 * Linear(value.G) + .0722 * Linear(value.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
