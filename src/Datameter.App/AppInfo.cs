namespace Datameter.App;

/// <summary>
/// What the app calls itself.
///
/// A Debug build says so in its window titles. A dev build and an installed one are the same
/// program with the same icon and the same taskbar entry, and testing against the wrong one
/// wastes real time — the name is the cheapest way to tell them apart.
/// </summary>
public static class AppInfo
{
#if DEBUG
    public const string DisplayName = "Datameter (dev)";
#else
    public const string DisplayName = "Datameter";
#endif

    /// <summary>The meter's window title. It never appears on the chip, which has no caption.</summary>
    public const string MeterWindowTitle = DisplayName + " speed meter";
}
