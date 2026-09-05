using Microsoft.Win32;

namespace Datameter.App;

/// <summary>
/// Whether Windows starts Datameter when you sign in.
///
/// This is kept in the HKCU Run key rather than in the app's own settings file, because Windows
/// is what acts on it: the registry is the state, and asking it is the only way to be sure the
/// switch reflects what will actually happen. It is per-user, so it never needs elevation.
///
/// The installer can also place a shortcut in the Startup folder. Both are honoured when
/// reporting the state, and both are removed when switching off, so the two can never disagree
/// or launch the app twice.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Datameter";

    /// <summary>The Startup-folder shortcut the installer offers to create.</summary>
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Datameter.lnk");

    public static bool IsEnabled()
    {
        try
        {
            if (File.Exists(ShortcutPath)) return true;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            // An unreadable registry should leave the switch off rather than take the app down.
            return false;
        }
    }

    /// <summary>
    /// Makes the recorded command line point at this copy of Datameter.
    ///
    /// The Run value records whichever copy switched it on, and nothing revalidates it. Reinstall
    /// somewhere else, or move the folder, and Windows keeps launching a path that is no longer
    /// there: the switch still reads as on and nothing starts, which is the worst of both. It
    /// also migrates an installer-made Startup shortcut to the Run value.
    /// </summary>
    public static void Reconcile()
    {
        try
        {
            if (!IsEnabled()) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var recorded = key?.GetValue(ValueName) as string;

            if (string.Equals(recorded, $"\"{exe}\"", StringComparison.OrdinalIgnoreCase)) return;

            Set(true);
        }
        catch
        {
        }
    }

    /// <summary>Returns whether the change took, so the switch can be put back if it did not.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;

                // Quoted: the install path contains no spaces today, but Windows splits on them
                // and a user can install anywhere.
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            // The installer's shortcut would start a second copy alongside the Run entry, so it
            // goes either way: switching on, this key takes over; switching off, nothing is left.
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
