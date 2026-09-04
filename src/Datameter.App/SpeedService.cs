using Datameter.Core;
using Microsoft.UI.Xaml;

namespace Datameter.App;

/// <summary>
/// One live-speed reading shared by every surface that shows it.
///
/// The meter and the main window must never disagree, and each sample is a difference between
/// two counter readings — so a second consumer sampling on its own clock would not merely cost
/// twice, it would report a different number over a different interval.
/// </summary>
public sealed class SpeedService
{
    private readonly SpeedMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new();

    public SpeedService()
    {
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            Latest = _monitor.Sample();
            Updated?.Invoke(this, Latest);
        };
    }

    /// <summary>The most recent reading, so a surface opening mid-stream starts with a figure.</summary>
    public SpeedSample Latest { get; private set; } = SpeedSample.Idle;

    public event EventHandler<SpeedSample>? Updated;

    public void Start()
    {
        if (_timer.IsEnabled) return;

        // The first sample only establishes a baseline, so take it now rather than showing a
        // dash for the first second.
        _monitor.Reset();
        _monitor.Sample();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
