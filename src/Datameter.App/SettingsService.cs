using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Datameter.App;

public sealed class Preferences
{
    /// <summary>"Default" follows the system, matching the Windows personalisation setting.</summary>
    public string Theme { get; set; } = nameof(ElementTheme.Default);
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
}
