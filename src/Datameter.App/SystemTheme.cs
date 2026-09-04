using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace Datameter.App;

/// <summary>
/// The light/dark setting Windows is currently on, and notice of it changing.
///
/// <see cref="ElementTheme.Default"/> is meant to mean "follow the system", and leaving it on
/// the root element delegates that to <c>Application.RequestedTheme</c>, which an unpackaged
/// WinUI 3 app resolves once at startup and never revisits. Resolving the system setting here
/// and applying it as a concrete theme makes "Use system setting" behave the way it reads:
/// correct on the first frame after installation, and correct again when the system flips.
/// </summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private readonly UISettings _settings = new();
    private readonly DispatcherQueue _queue;
    private bool _disposed;

    public SystemThemeWatcher(DispatcherQueue queue)
    {
        _queue = queue;

        try
        {
            // Raised on a pool thread, and for accent-colour changes as well as light/dark, so
            // the handler marshals back and simply re-reads rather than trusting the argument.
            _settings.ColorValuesChanged += OnColorValuesChanged;
        }
        catch
        {
            // Without the notification the theme still resolves; it just stops tracking changes.
        }
    }

    public event EventHandler? Changed;

    /// <summary>
    /// The system app theme, or <see cref="ElementTheme.Default"/> if it cannot be read — in
    /// which case the framework's own resolution is the better answer than a guess.
    /// </summary>
    public static ElementTheme Current()
    {
        try
        {
            var background = new UISettings().GetColorValue(UIColorType.Background);

            // Windows reports white for light and near-black for dark. Comparing brightness
            // rather than equality keeps this working against high-contrast variations.
            var brightness = (background.R * 0.299) + (background.G * 0.587) + (background.B * 0.114);
            return brightness > 127 ? ElementTheme.Light : ElementTheme.Dark;
        }
        catch
        {
            return ElementTheme.Default;
        }
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        if (_disposed) return;
        _queue.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _settings.ColorValuesChanged -= OnColorValuesChanged; }
        catch { }
    }
}
