using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Datameter.App;

/// <summary>
/// Colours for anything drawn from code.
///
/// XAML gets its colours from {ThemeResource}, which honours an element's RequestedTheme.
/// Code cannot: Application.Current.Resources always answers for the *system* theme, so a
/// lookup there returns dark brushes even while the window is showing Light. Everything the
/// code draws therefore picks its colour from here, keyed on the element's ActualTheme.
/// </summary>
public static class Palette
{
    /// <summary>
    /// Per-network colours. A network's index is persisted, so its colour never reshuffles;
    /// the two rows are the same hues tuned for contrast on each ground.
    /// </summary>
    private static readonly Color[] NetworksDark =
    {
        Rgb(0x4C, 0xC2, 0xFF), Rgb(0xFF, 0xA9, 0x4D), Rgb(0x5C, 0xD6, 0xA9), Rgb(0xC5, 0x8A, 0xF9),
        Rgb(0xFF, 0x7B, 0x8A), Rgb(0xE8, 0xD4, 0x5C), Rgb(0x7B, 0xA7, 0xF5), Rgb(0x9B, 0xD6, 0x5C)
    };

    /// <summary>
    /// The light row is deliberately more saturated than the dark one. These are fills and
    /// dots rather than text, so they can be vivid on white without hurting legibility — and
    /// muted versions read as washed-out against a bright page.
    /// </summary>
    private static readonly Color[] NetworksLight =
    {
        Rgb(0x00, 0x91, 0xFF), Rgb(0xFF, 0x8A, 0x00), Rgb(0x00, 0xB8, 0x88), Rgb(0x9B, 0x4D, 0xFF),
        Rgb(0xFF, 0x3B, 0x60), Rgb(0xE8, 0xB4, 0x00), Rgb(0x2F, 0x6B, 0xFF), Rgb(0x56, 0xC8, 0x00)
    };

    public static bool IsLight(ElementTheme theme) => theme == ElementTheme.Light;

    public static Brush Network(ElementTheme theme, int index)
    {
        var set = IsLight(theme) ? NetworksLight : NetworksDark;
        return new SolidColorBrush(set[((index % set.Length) + set.Length) % set.Length]);
    }

    public static Brush Chart(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0x00, 0x91, 0xFF) : Rgb(0x4C, 0xC2, 0xFF));

    public static Brush CardBackground(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0xFB, 0xFB, 0xFB) : Rgb(0x2B, 0x2B, 0x2B));

    public static Brush CardStroke(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0xE5, 0xE5, 0xE5) : Rgb(0x3A, 0x3A, 0x3A));

    public static Brush TextPrimary(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0x1B, 0x1B, 0x1B) : Colors.White);

    public static Brush TextSecondary(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0x40, 0x40, 0x40) : Rgb(0xC8, 0xC8, 0xC8));

    public static Brush TextTertiary(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0x6E, 0x6E, 0x6E) : Rgb(0x96, 0x96, 0x96));

    public static Brush Accent(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0x00, 0x5F, 0xB8) : Rgb(0x4C, 0xC2, 0xFF));

    /// <summary>Text drawn on top of <see cref="Accent"/>.</summary>
    public static Brush OnAccent(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Colors.White : Rgb(0x00, 0x33, 0x54));

    /// <summary>
    /// The plate behind an app logo.
    ///
    /// Packaged apps ship a transparent logo plus the background colour it is meant to sit on
    /// — that pairing is what the Start menu draws, and it is the only thing that makes a
    /// white-on-transparent logo legible. When a package declares one, it wins. Everything
    /// else gets the same quiet tile, because per-icon plate colours make the list look
    /// arbitrary: one row white, the next dark, for reasons the reader cannot see.
    /// </summary>
    public static Brush IconPlate(ElementTheme theme, Color? declared)
    {
        if (declared is { } color) return new SolidColorBrush(color);
        return new SolidColorBrush(IsLight(theme) ? Rgb(0xEF, 0xEF, 0xEF) : Rgb(0x38, 0x38, 0x38));
    }

    /// <summary>The flat bar a skeleton placeholder is made of.</summary>
    public static Brush SkeletonBase(ElementTheme theme) =>
        new SolidColorBrush(IsLight(theme) ? Rgb(0xD9, 0xD9, 0xDE) : Rgb(0x35, 0x37, 0x3B));

    /// <summary>
    /// The sheen that travels across a placeholder. It is a soft band rather than a hard edge,
    /// and lighter in light mode where the base bar is already pale.
    /// </summary>
    public static Brush SkeletonSheen(ElementTheme theme)
    {
        var peak = IsLight(theme)
            ? Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);

        var clear = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
            GradientStops =
            {
                new GradientStop { Color = clear, Offset = 0.0 },
                new GradientStop { Color = peak,  Offset = 0.5 },
                new GradientStop { Color = clear, Offset = 1.0 }
            }
        };
    }

    public static Brush Transparent() => new SolidColorBrush(Colors.Transparent);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
}
