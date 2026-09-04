using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Datameter.App;

/// <summary>How large the floating meter is drawn. One setting moves card, text and glyphs.</summary>
public enum MeterSizeOption
{
    Small,
    Medium,
    Large
}

public sealed class Preferences
{
    /// <summary>"Default" follows the system, matching the Windows personalisation setting.</summary>
    public string Theme { get; set; } = nameof(ElementTheme.Default);

    /// <summary>
    /// Label of the period last looked at. Null means this install has never chosen one, and a
    /// first run opens on <see cref="DefaultPeriod"/> rather than the widest span available.
    /// </summary>
    public string? Period { get; set; }

    /// <summary>Bounds of the custom range, so choosing it again returns to the same window.</summary>
    public DateTimeOffset? CustomFrom { get; set; }
    public DateTimeOffset? CustomTo { get; set; }

    /// <summary>Whether the floating speed meter is on screen.</summary>
    public bool ShowSpeedMeter { get; set; } = true;

    /// <summary>
    /// Where the meter was left, in physical screen pixels. Null puts it in its default corner,
    /// and a position that no longer lands on a connected display is discarded on load.
    /// </summary>
    public int? MeterX { get; set; }
    public int? MeterY { get; set; }

    /// <summary>Size of the floating meter.</summary>
    public string MeterSize { get; set; } = nameof(MeterSizeOption.Medium);

    /// <summary>What a fresh install opens on.</summary>
    public const string DefaultPeriod = "Today";
}

/// <summary>Small JSON file beside the usage database. Never throws at the caller.</summary>
public static class SettingsService
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Datameter", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Preferences Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path)) ?? new Preferences();
        }
        catch
        {
            // A corrupt settings file should cost you your preferences, not your app.
        }
        return new Preferences();
    }

    public static void Save(Preferences preferences)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(preferences, Options));
        }
        catch
        {
        }
    }

    public static ElementTheme ParseTheme(string? value) =>
        Enum.TryParse<ElementTheme>(value, out var theme) ? theme : ElementTheme.Default;

    public static MeterSizeOption ParseMeterSize(string? value) =>
        Enum.TryParse<MeterSizeOption>(value, out var size) ? size : MeterSizeOption.Medium;
}
