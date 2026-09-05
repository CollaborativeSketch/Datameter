using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Datameter.Core;
using Microsoft.UI.Xaml.Media;

namespace Datameter.App;

public enum ChartGrain { Hour, Day, Month }

/// <summary>
/// A selectable window of time. Calendar periods ("this month") can't be expressed as a fixed
/// span, so each option resolves its own bounds from the current local time.
/// </summary>
public sealed record PeriodOption(
    string Label,
    ChartGrain Grain,
    Func<DateTimeOffset, (DateTimeOffset From, DateTimeOffset To)> Resolve)
{
    public override string ToString() => Label;
}

/// <summary>One slice of the contribution bar.</summary>
public sealed record SegmentVm(string Name, int ColorIndex, double Share, string ValueText, string PercentText);

/// <summary>One network tile.</summary>
public sealed record NetworkVm(
    long Id, string Name, int ColorIndex, string ValueText, string PercentText,
    double BarPercent, NetworkKind Kind, bool IsMetered, bool IsSelected);

/// <summary>
/// One row of the per-app breakdown. Mutable, because the icon arrives after the row does —
/// pulling a logo off disk is far slower than reading the byte counts.
/// </summary>
public sealed class AppVm : INotifyPropertyChanged
{
    public AppVm(string name, string attributionId, string valueText, double barPercent)
    {
        Name = name;
        AttributionId = attributionId;
        ValueText = valueText;
        BarPercent = barPercent;
    }

    public string Name { get; }
    public string AttributionId { get; }
    public string ValueText { get; }
    public double BarPercent { get; }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Raise(nameof(Icon));
            Raise(nameof(HasIcon));
            Raise(nameof(NoIcon));
        }
    }

    /// <summary>The background colour the app's package declares for its logo, if any.</summary>
    public Windows.UI.Color? IconPlateColor { get; set; }

    /// <summary>
    /// How large to draw the logo inside its 28px plate. A packaged logo already carries its
    /// own padding — it is drawn to sit edge-to-edge on the tile colour — so insetting it
    /// again leaves a tiny glyph adrift in the middle. Desktop icons have no such padding and
    /// do need the inset.
    /// </summary>
    private double _iconSize = 20;
    public double IconSize
    {
        get => _iconSize;
        set { _iconSize = value; Raise(nameof(IconSize)); }
    }

    private Brush? _iconPlate;
    public Brush? IconPlate
    {
        get => _iconPlate;
        set { _iconPlate = value; Raise(nameof(IconPlate)); }
    }

    public bool HasIcon => _icon is not null;
    public bool NoIcon => _icon is null;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One bar of the usage chart.</summary>
public sealed record BarVm(double Ratio, string Label);

/// <summary>
/// One rule on the chart's vertical ruler. <paramref name="Fraction"/> is measured from the
/// baseline, so 1.0 is the top of the plot.
/// </summary>
public sealed record ChartTick(double Fraction, string Label);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly UsageStore _store;

    /// <summary>The label the custom-range option carries; also how the UI spots it.</summary>
    public const string CustomRangeLabel = "Custom range";

    public MainViewModel(UsageStore store, Preferences preferences)
    {
        _store = store;

        // Sensible starting bounds, so the pickers are never empty when first revealed.
        _customTo = preferences.CustomTo ?? DateTimeOffset.Now.Date;
        _customFrom = preferences.CustomFrom ?? _customTo.AddDays(-7);

        Periods = new ObservableCollection<PeriodOption>(BuildPeriods());

        // A fresh install opens on Today. After that the period last looked at is restored, so
        // the app comes back to where it was left rather than to a fixed default. An unknown
        // label — a period renamed by a later version — falls back rather than throwing.
        _selectedPeriod =
            Periods.FirstOrDefault(p => p.Label == preferences.Period)
            ?? Periods.First(p => p.Label == Preferences.DefaultPeriod);
    }

    private DateTimeOffset _customFrom;
    public DateTimeOffset CustomFrom
    {
        get => _customFrom;
        set
        {
            // Keep the range the right way round rather than returning nothing.
            if (value > _customTo) _customTo = value;
            if (Set(ref _customFrom, value) && IsCustomRange) Refresh();
        }
    }

    private DateTimeOffset _customTo;
    public DateTimeOffset CustomTo
    {
        get => _customTo;
        set
        {
            if (value < _customFrom) _customFrom = value;
            if (Set(ref _customTo, value) && IsCustomRange) Refresh();
        }
    }

    public bool IsCustomRange => _selectedPeriod.Label == CustomRangeLabel;

    private IEnumerable<PeriodOption> BuildPeriods()
    {
        // Bounds are resolved in local time — "today" means the user's midnight, not UTC's.
        yield return new("Today", ChartGrain.Hour, now =>
            (now.Date, now));

        yield return new("Yesterday", ChartGrain.Hour, now =>
            (now.Date.AddDays(-1), now.Date));

        yield return new("Last 24 hours", ChartGrain.Hour, now =>
            (now.AddHours(-24), now));

        yield return new("Last 7 days", ChartGrain.Day, now =>
            (now.Date.AddDays(-6), now));

        yield return new("This month", ChartGrain.Day, now =>
            (FirstOfMonth(now), now));

        yield return new("Last month", ChartGrain.Day, now =>
            (FirstOfMonth(now).AddMonths(-1), FirstOfMonth(now)));

        yield return new("Last 30 days", ChartGrain.Day, now =>
            (now.AddDays(-30), now));

        yield return new("Last 12 months", ChartGrain.Month, now =>
            (now.AddDays(-365), now));

        // The end date is inclusive: picking 5 Sept means through the end of 5 Sept.
        yield return new(CustomRangeLabel, ChartGrain.Day, _ =>
            (_customFrom.Date, _customTo.Date.AddDays(1)));

        static DateTimeOffset FirstOfMonth(DateTimeOffset t) =>
            new(t.Year, t.Month, 1, 0, 0, 0, t.Offset);
    }

    public ObservableCollection<PeriodOption> Periods { get; }
    public ObservableCollection<SegmentVm> Segments { get; } = new();
    public ObservableCollection<NetworkVm> Networks { get; } = new();
    public ObservableCollection<BarVm> Chart { get; } = new();
    public ObservableCollection<AppVm> Apps { get; } = new();

    /// <summary>How many app rows to show before the "Show all apps" button takes over.</summary>
    public const int AppPreviewCount = 8;

    private IReadOnlyList<AppUsage> _allApps = Array.Empty<AppUsage>();
    private bool _showAllApps;

    private PeriodOption _selectedPeriod;
    public PeriodOption SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            if (value is null || !Set(ref _selectedPeriod, value)) return;
            OnPropertyChanged(nameof(IsCustomRange));
            Refresh();
        }
    }

    /// <summary>
    /// Networks the view is narrowed to. Empty means every network combined — selecting none
    /// and selecting all show the same totals, so the empty set is the natural "no filter".
    /// </summary>
    private readonly HashSet<long> _selectedNetworkIds = new();

    public IReadOnlyCollection<long> SelectedNetworkIds => _selectedNetworkIds;

    /// <summary>Names of the selected networks, in the order they appear on screen.</summary>
    public IReadOnlyList<string> SelectedNetworkNames { get; private set; } = Array.Empty<string>();

    private bool _isFiltered;
    public bool IsFiltered { get => _isFiltered; private set => Set(ref _isFiltered, value); }

    /// <summary>Clicking a selected network again deselects it.</summary>
    public void ToggleNetwork(long id)
    {
        if (!_selectedNetworkIds.Remove(id)) _selectedNetworkIds.Add(id);
        Refresh();
    }

    public void ClearNetworkFilter()
    {
        if (_selectedNetworkIds.Count == 0) return;
        _selectedNetworkIds.Clear();
        Refresh();
    }

    /// <summary>Resolved bounds of the current selection, in UTC.</summary>
    public (DateTimeOffset From, DateTimeOffset To) CurrentRangeUtc()
    {
        var (from, to) = _selectedPeriod.Resolve(DateTimeOffset.Now);
        return (from.ToUniversalTime(), to.ToUniversalTime());
    }

    private string _totalValue = "—";
    public string TotalValue { get => _totalValue; private set => Set(ref _totalValue, value); }

    private string _totalUnit = "";
    public string TotalUnit { get => _totalUnit; private set => Set(ref _totalUnit, value); }

    private string _sentText = "";
    public string SentText { get => _sentText; private set => Set(ref _sentText, value); }

    private string _receivedText = "";
    public string ReceivedText { get => _receivedText; private set => Set(ref _receivedText, value); }

    private string _subtitle = "";
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    private string _chartTitle = "Daily usage";
    public string ChartTitle { get => _chartTitle; private set => Set(ref _chartTitle, value); }

    private string _axisStart = "";
    public string AxisStart { get => _axisStart; private set => Set(ref _axisStart, value); }

    private string _axisEnd = "";
    public string AxisEnd { get => _axisEnd; private set => Set(ref _axisEnd, value); }

    /// <summary>Rules for the chart's vertical ruler, top of the plot first.</summary>
    private IReadOnlyList<ChartTick> _chartTicks = Array.Empty<ChartTick>();
    public IReadOnlyList<ChartTick> ChartTicks { get => _chartTicks; private set => Set(ref _chartTicks, value); }

    private string _speedUp = "0.0 KB/s";
    public string SpeedUp { get => _speedUp; private set => Set(ref _speedUp, value); }

    private string _speedDown = "0.0 KB/s";
    public string SpeedDown { get => _speedDown; private set => Set(ref _speedDown, value); }

    /// <summary>The live reading, which is measured rather than recorded — see SpeedMonitor.</summary>
    public void SetSpeed(SpeedSample sample, SpeedUnit unit)
    {
        SpeedUp = ByteFormat.HumanizeRate(sample.SentPerSecond, unit);
        SpeedDown = ByteFormat.HumanizeRate(sample.ReceivedPerSecond, unit);
    }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _appsNote = "";
    public string AppsNote { get => _appsNote; set => Set(ref _appsNote, value); }

    private string _appsTitle = "Usage by app";
    public string AppsTitle { get => _appsTitle; private set => Set(ref _appsTitle, value); }

    private bool _hasMoreApps;
    public bool HasMoreApps { get => _hasMoreApps; private set => Set(ref _hasMoreApps, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    /// <summary>
    /// True while the per-app query is out. It always takes seconds — the API has to be asked
    /// live, per network — so the list shows placeholder rows rather than an empty gap.
    /// </summary>
    private bool _isLoadingApps;
    public bool IsLoadingApps
    {
        get => _isLoadingApps;
        set { if (Set(ref _isLoadingApps, value)) OnPropertyChanged(nameof(ShowAppList)); }
    }

    /// <summary>The real list hides while placeholders are up, so the two never stack.</summary>
    public bool ShowAppList => !_isLoadingApps;

    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    /// <summary>Raised after Segments/Chart change, so the code-behind can rebuild proportional layout.</summary>
    public event EventHandler? VisualsChanged;

    public void Refresh()
    {
        var (fromUtc, toUtc) = CurrentRangeUtc();
        var period = _selectedPeriod;

        // UsageStore serialises its own access, so this is safe from any thread.
        var networks = _store.GetTotals(fromUtc, toUtc);
        var earliest = _store.GetEarliestRecordedHour();

        var active = networks.Where(n => n.Total > 0).OrderByDescending(n => n.Total).ToList();

        // Selections that recorded nothing this period are dropped rather than leaving the
        // screen empty with no obvious way back.
        _selectedNetworkIds.IntersectWith(active.Select(n => n.NetworkId));

        var selected = active.Where(n => _selectedNetworkIds.Contains(n.NetworkId)).ToList();
        SelectedNetworkNames = selected.Select(n => n.ProfileName).ToList();
        IsFiltered = selected.Count > 0;

        var counted = IsFiltered ? selected : active;
        var sent = counted.Sum(n => n.BytesSent);
        var received = counted.Sum(n => n.BytesReceived);
        var total = sent + received;

        IsEmpty = total == 0;

        // Headline. Split the unit off so it can be typeset smaller beside the figure.
        var (value, unit) = SplitHumanized(total);
        TotalValue = value;
        TotalUnit = unit;
        SentText = ByteFormat.Humanize(sent);
        ReceivedText = ByteFormat.Humanize(received);

        Subtitle = BuildSubtitle(active.Count, SelectedNetworkNames, fromUtc, toUtc, earliest, total);
        AppsTitle = SelectedNetworkNames.Count switch
        {
            0 => "Usage by app",
            1 => $"Usage by app on {SelectedNetworkNames[0]}",
            _ => $"Usage by app on {SelectedNetworkNames.Count} networks"
        };

        var grandTotal = active.Sum(n => n.Total);
        Segments.Clear();
        Networks.Clear();
        var max = active.Count > 0 ? active[0].Total : 1;

        // The bar covers exactly what the headline figure covers, so a selection fills the width
        // rather than leaving unselected networks drawn but dimmed.
        foreach (var n in counted)
        {
            var share = total > 0 ? 100d * n.Total / total : 0;
            Segments.Add(new SegmentVm(
                n.ProfileName, n.ColorIndex, Math.Max(share, 0.35),
                ByteFormat.Humanize(n.Total), $"{share:N1}%"));
        }

        // Tiles always show a network's share of everything, so the numbers you pick from stay
        // put rather than rewriting themselves each time the selection changes.
        foreach (var n in active)
        {
            var pct = grandTotal > 0 ? 100d * n.Total / grandTotal : 0;
            Networks.Add(new NetworkVm(
                n.NetworkId, n.ProfileName, n.ColorIndex, ByteFormat.Humanize(n.Total), $"{pct:N1}%",
                Math.Max(2d, 100d * n.Total / max), n.Kind, n.IsMetered,
                _selectedNetworkIds.Contains(n.NetworkId)));
        }

        var series = _store.GetCombinedSeries(fromUtc, toUtc, _selectedNetworkIds);
        BuildChart(series, period);
        VisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string BuildSubtitle(
        int networkCount, IReadOnlyList<string> selectedNames,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset? earliest, long total)
    {
        if (total == 0) return "No usage recorded in this period";

        // If the window reaches past what we hold, say so rather than under-reporting silently.
        var truncated = earliest is not null && earliest > fromUtc;
        var shownFrom = (truncated ? earliest!.Value : fromUtc).ToLocalTime();

        // Ranges end exclusively, on the following midnight. Report the last day the user
        // actually asked for, so "to 1 Sept" never reads back as "– 2 Sept".
        var shownTo = toUtc.ToLocalTime();
        if (shownTo.TimeOfDay == TimeSpan.Zero) shownTo = shownTo.AddSeconds(-1);

        var range = shownFrom.Date == shownTo.Date
            ? $"{shownFrom:d MMM yyyy}"
            : $"{shownFrom:d MMM} – {shownTo:d MMM yyyy}";

        if (truncated) range = $"records begin {shownFrom:d MMM yyyy}";

        // Name one or two selections outright; past that a count reads better than a list.
        var scope = selectedNames.Count switch
        {
            0 => $"Across {networkCount} {(networkCount == 1 ? "network" : "networks")}",
            1 => selectedNames[0],
            2 => $"{selectedNames[0]} and {selectedNames[1]}",
            _ => $"{selectedNames.Count} selected networks"
        };

        return $"{scope} · {range}";
    }

    private void BuildChart(IReadOnlyList<UsageBucket> series, PeriodOption period)
    {
        Chart.Clear();

        var buckets = period.Grain switch
        {
            ChartGrain.Hour => series
                .Select(b => (Value: (double)b.Total, Label: b.HourUtc.ToLocalTime().ToString("HH:00")))
                .ToList(),

            ChartGrain.Day => series
                .GroupBy(b => b.HourUtc.ToLocalTime().Date)
                .OrderBy(g => g.Key)
                .Select(g => (Value: (double)g.Sum(x => x.Total), Label: g.Key.ToString("d MMM")))
                .ToList(),

            _ => series
                .GroupBy(b => new DateTime(b.HourUtc.ToLocalTime().Year, b.HourUtc.ToLocalTime().Month, 1))
                .OrderBy(g => g.Key)
                .Select(g => (Value: (double)g.Sum(x => x.Total), Label: g.Key.ToString("MMM yyyy")))
                .ToList(),
        };

        ChartTitle = period.Grain switch
        {
            ChartGrain.Hour => "Hourly usage",
            ChartGrain.Day => "Daily usage",
            _ => "Monthly usage"
        };

        if (buckets.Count == 0)
        {
            AxisStart = AxisEnd = "";
            ChartTicks = Array.Empty<ChartTick>();
            return;
        }

        // Bars are drawn against a ruled maximum rather than against the tallest bar. Scaling to
        // the peak would put the busiest bucket at full height in every period, which reads as
        // "this is as high as it goes" whatever the actual figure was.
        var peak = Math.Max(buckets.Max(b => b.Value), 1);
        var ceiling = NiceCeiling(peak);

        foreach (var b in buckets)
            Chart.Add(new BarVm(b.Value / ceiling, $"{b.Label} · {ByteFormat.Humanize((long)b.Value)}"));

        ChartTicks = BuildTicks(ceiling);
        AxisStart = buckets[0].Label;
        AxisEnd = buckets[^1].Label;
    }

    /// <summary>How many parts the ruler divides the plot into.</summary>
    private const int RulerDivisions = 4;

    /// <summary>
    /// Rounds a peak up to a figure the ruler can be labelled with.
    ///
    /// The rounding happens inside the unit the label will be written in — so a 700 MB peak
    /// rules to 800 MB rather than to some exact byte count that reads as noise — and the steps
    /// are chosen to divide cleanly into quarters, because that is how the ruler is subdivided.
    /// </summary>
    private static double NiceCeiling(double peak)
    {
        if (peak <= 0) return 1;

        var unit = Math.Pow(1024, Math.Floor(Math.Log(peak, 1024)));
        var scaled = peak / unit;

        var basis = Math.Pow(10, Math.Floor(Math.Log10(scaled)));
        var normalized = scaled / basis;

        var step = normalized switch
        {
            <= 1 => 1d,
            <= 2 => 2d,
            <= 4 => 4d,
            <= 5 => 5d,
            <= 8 => 8d,
            _ => 10d
        };

        return step * basis * unit;
    }

    private static IReadOnlyList<ChartTick> BuildTicks(double ceiling)
    {
        var ticks = new List<ChartTick>(RulerDivisions + 1);

        // Top first, because that is the order they are drawn down the ruler.
        for (var i = RulerDivisions; i >= 0; i--)
        {
            var fraction = i / (double)RulerDivisions;
            // Every mark is written in the unit the top of the ruler is written in, so the
            // column reads as one scale rather than changing unit halfway down.
            var label = i == 0
                ? "0"
                : ByteFormat.HumanizeIn((long)Math.Round(ceiling * fraction), (long)Math.Round(ceiling));
            ticks.Add(new ChartTick(fraction, label));
        }

        return ticks;
    }

    // ---- per-app breakdown ---------------------------------------------------

    /// <summary>
    /// Replaces the per-app breakdown. Windows only attributes traffic to apps for as long as
    /// it keeps its own history, so this is always the live figure rather than something we
    /// can accumulate — the note says so when the selected period reaches further back.
    /// </summary>
    public void SetApps(IReadOnlyList<AppUsage> apps, bool truncatedByRetention)
    {
        _allApps = apps;
        _showAllApps = false;
        RebuildApps();

        AppsNote = apps.Count == 0
            ? ""
            : truncatedByRetention
                ? $"{apps.Count} apps · Windows attributes apps for the last ~30 days only"
                : $"{apps.Count} apps";
    }

    public void ShowAllApps()
    {
        _showAllApps = true;
        RebuildApps();
    }

    private void RebuildApps()
    {
        Apps.Clear();
        if (_allApps.Count == 0)
        {
            HasMoreApps = false;
            return;
        }

        var max = _allApps[0].Total;
        var take = _showAllApps ? _allApps.Count : Math.Min(AppPreviewCount, _allApps.Count);

        for (int i = 0; i < take; i++)
        {
            var a = _allApps[i];
            // The raw attribution id is kept as-is so icons can be matched back to it.
            Apps.Add(new AppVm(
                a.Name,
                a.AttributionId,
                ByteFormat.Humanize(a.Total),
                max > 0 ? Math.Max(1d, 100d * a.Total / max) : 0));
        }

        HasMoreApps = take < _allApps.Count;
        AppsChanged?.Invoke(this, _allApps);
    }

    /// <summary>Raised when the app rows are replaced, so the page can go and fetch their logos.</summary>
    public event EventHandler<IReadOnlyList<AppUsage>>? AppsChanged;

    /// <summary>"96.93 GB" becomes ("96.93", "GB") so the unit can be set smaller.</summary>
    private static (string Value, string Unit) SplitHumanized(long bytes)
    {
        var text = ByteFormat.Humanize(bytes);
        var space = text.LastIndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}
