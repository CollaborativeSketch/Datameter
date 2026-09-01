using Datameter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System.Reflection;

namespace Datameter.App;

public sealed partial class MainPage : UserControl
{
    private readonly UsageStore _store;
    private readonly UsageProvider _provider = new();
    private readonly AppUsageProvider _appProvider = new();
    private readonly AppIconLoader _icons = new();
    private readonly SyncService _sync;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly Preferences _preferences;

    /// <summary>
    /// Per-app usage cannot be cached by hour, so each period costs a live query (~15s).
    /// Results are held for the session so switching back is instant. Keyed by period and
    /// the drilled-into network, since both change the answer.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<AppUsage>> _appCache = new();

    /// <summary>Drives the sheen across the skeleton placeholders while apps are loading.</summary>
    private readonly Storyboard _shimmer = new();

    private CancellationTokenSource? _appQuery;
    private bool _syncing;
    private bool _settingTheme;
    private bool _settingDates;

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Read from the assembly rather than written into the XAML, so the About block cannot
    /// drift out of step with what was actually built.
    /// </summary>
    private static string AppVersion
    {
        get
        {
            var attribute = (AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));

            // Strip any "+<commit sha>" suffix the SDK appends.
            return attribute?.InformationalVersion.Split('+')[0] ?? "";
        }
    }

    public MainPage()
    {
        InitializeComponent();

        _store = new UsageStore(UsageStore.DefaultPath);
        _sync = new SyncService(_provider, _store);
        ViewModel = new MainViewModel(_store);

        ViewModel.VisualsChanged += (_, _) =>
        {
            RebuildContributionBar();
            RebuildNetworkChips();
            RebuildChart();
            _ = LoadAppsAsync();
        };

        ViewModel.AppsChanged += (_, apps) => _ = LoadIconsAsync(apps);

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLoadingApps)) UpdateSkeletonState();
        };

        _preferences = SettingsService.Load();
        VersionText.Text = $"Version {AppVersion}";

        PageRoot.SizeChanged += (_, _) =>
        {
            RebuildContributionBar();
            RebuildNetworkChips();
            RebuildChart();
        };

        // Catches the system flipping light/dark while "Use system setting" is selected.
        PageRoot.ActualThemeChanged += (_, _) => RepaintForTheme();

        // Windows attributes bytes to the hour as it goes, so a periodic poll keeps us current.
        _refreshTimer.Interval = TimeSpan.FromMinutes(15);
        _refreshTimer.Tick += async (_, _) => await SyncAsync(full: false);
        _refreshTimer.Start();

        Loaded += async (_, _) =>
        {
            try
            {
                ApplySavedTheme();
                SyncDatePickers();
                await StartupAsync();
            }
            catch (Exception ex)
            {
                App.Log("Startup", ex);
                ViewModel.Status = $"Startup failed: {ex.Message}";
                ViewModel.IsBusy = false;
            }
        };
    }

    // ---- theme ---------------------------------------------------------------

    private void ApplySavedTheme()
    {
        var theme = SettingsService.ParseTheme(_preferences.Theme);

        _settingTheme = true;   // suppress the Checked handler while we reflect saved state
        (theme switch
        {
            ElementTheme.Light => ThemeLight,
            ElementTheme.Dark => ThemeDark,
            _ => ThemeSystem
        }).IsChecked = true;
        _settingTheme = false;

        ApplyTheme(theme);
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_settingTheme) return;

        var theme = SettingsService.ParseTheme((string)((RadioButton)sender).Tag);
        ApplyTheme(theme);

        _preferences.Theme = theme.ToString();
        SettingsService.Save(_preferences);
    }

    /// <summary>
    /// RequestedTheme has to be set on the window's root element; setting it on this control
    /// alone would leave the title bar and backdrop on the old theme.
    /// </summary>
    private void ApplyTheme(ElementTheme theme)
    {
        if (XamlRoot?.Content is FrameworkElement root)
            root.RequestedTheme = theme;
        else
            RequestedTheme = theme;

        // ActualThemeChanged does the repaint, but it does not fire when the requested theme
        // resolves to the one already showing — so ask for a repaint either way.
        DispatcherQueue.TryEnqueue(RepaintForTheme);
    }

    /// <summary>Repaints everything the code draws, after the theme changes under it.</summary>
    private void RepaintForTheme()
    {
        RebuildContributionBar();
        RebuildNetworkChips();
        RebuildChart();
        RepaintIconPlates();

        // The skeleton's bars and sheen are theme-coloured too.
        if (ViewModel.IsLoadingApps) UpdateSkeletonState();
    }

    // ---- custom range --------------------------------------------------------

    private void SyncDatePickers()
    {
        _settingDates = true;
        CustomFromPicker.Date = ViewModel.CustomFrom;
        CustomToPicker.Date = ViewModel.CustomTo;
        CustomToPicker.MaxDate = DateTimeOffset.Now.Date;
        _settingDates = false;
    }

    private void OnCustomRangeChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_settingDates || args.NewDate is null) return;

        if (ReferenceEquals(sender, CustomFromPicker))
            ViewModel.CustomFrom = args.NewDate.Value;
        else
            ViewModel.CustomTo = args.NewDate.Value;

        // The view model keeps the two ends in order; mirror any correction back to the pickers.
        SyncDatePickers();
    }

    private void OnClearNetworkFilter(object sender, RoutedEventArgs e) => ViewModel.ClearNetworkFilter();

    // ---- startup and sync ----------------------------------------------------

    private async Task StartupAsync()
    {
        // Show whatever is cached immediately — the first paint must never wait on Windows.
        ViewModel.Refresh();

        var firstRun = _store.GetEarliestRecordedHour() is null;

        if (firstRun && ArchiveImporter.ArchiveExists())
        {
            ViewModel.IsBusy = true;
            ViewModel.Status = "Recovering history from the Data usage archive…";

            var result = await Task.Run(() => new ArchiveImporter(_store).Import());

            if (result.HoursImported > 0)
            {
                ViewModel.Refresh();
                ViewModel.Status = $"Recovered {result.HoursImported:N0} hours back to {result.Earliest:MMM yyyy}";
            }
        }

        await SyncAsync(full: firstRun);
    }

    private async Task SyncAsync(bool full)
    {
        if (_syncing) return;
        _syncing = true;

        try
        {
            ViewModel.IsBusy = true;
            ViewModel.Status = full
                ? "Reading every remembered network for the first time…"
                : "Checking for new usage…";

            // Progress<T> captures this (UI) context, so reports marshal back automatically.
            var progress = new Progress<SyncProgress>(p =>
                ViewModel.Status = $"Reading {p.ProfileName} ({p.Index} of {p.Total})…");

            await Task.Run(() => _sync.SyncAsync(full, progress));

            _appCache.Clear();   // new bytes landed; per-app figures are now stale
            ViewModel.Refresh();
            ViewModel.Status = "";
        }
        catch (Exception ex)
        {
            App.Log("Sync", ex);
            ViewModel.Status = $"Could not read usage: {ex.Message}";
        }
        finally
        {
            ViewModel.IsBusy = false;
            _syncing = false;
        }
    }

    // ---- per-app usage -------------------------------------------------------

    private async Task LoadAppsAsync()
    {
        var (fromUtc, toUtc) = ViewModel.CurrentRangeUtc();
        var selectedNames = ViewModel.SelectedNetworkNames.ToHashSet(StringComparer.Ordinal);
        var scope = string.Join(",", ViewModel.SelectedNetworkIds.OrderBy(id => id));
        var key = $"{ViewModel.SelectedPeriod.Label}|{fromUtc:O}|{toUtc:O}|{scope}";

        // Beyond the API's reach the answer is necessarily partial; say so rather than imply completeness.
        var truncated = DateTimeOffset.UtcNow - fromUtc > UsageProvider.MaxQuerySpan;

        if (_appCache.TryGetValue(key, out var cached))
        {
            ViewModel.IsLoadingApps = false;
            ViewModel.SetApps(cached, truncated);
            return;
        }

        _appQuery?.Cancel();
        var cts = new CancellationTokenSource();
        _appQuery = cts;

        try
        {
            ViewModel.SetApps(Array.Empty<AppUsage>(), false);
            ViewModel.IsLoadingApps = true;
            ViewModel.AppsNote = "Reading app usage…";

            // Ask only the networks that carried traffic in *this* period, not every network
            // that ever has. Each one costs ~3 seconds, and the imported history had grown the
            // "ever used" list well past what any single window needs.
            var wanted = selectedNames.Count > 0
                ? selectedNames
                : ViewModel.Networks.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

            var apps = await Task.Run(async () =>
            {
                var handles = _provider.EnumerateProfiles()
                    .Where(h => wanted.Contains(h.ProfileName))
                    .ToList();

                return await _appProvider.GetAsync(handles, fromUtc, toUtc, cts.Token).ConfigureAwait(false);
            }, cts.Token);

            if (cts.IsCancellationRequested) return;

            _appCache[key] = apps;
            ViewModel.IsLoadingApps = false;
            ViewModel.SetApps(apps, truncated);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection; the newer query owns the UI and its spinner.
        }
        catch (Exception ex)
        {
            App.Log("AppUsage", ex);
            ViewModel.IsLoadingApps = false;
            ViewModel.AppsNote = "App usage unavailable";
        }
    }

    /// <summary>
    /// Fetches a logo per visible row. Rows render immediately with a placeholder glyph and
    /// swap in their icon as it arrives, so a slow disk never holds up the numbers.
    /// </summary>
    private async Task LoadIconsAsync(IReadOnlyList<AppUsage> apps)
    {
        var byId = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps) byId[a.AttributionId ?? ""] = a;

        var theme = PageRoot.ActualTheme;

        foreach (var row in ViewModel.Apps.ToList())
        {
            if (row.Icon is not null) continue;
            if (!byId.TryGetValue(row.AttributionId ?? "", out var usage)) continue;

            var icon = await _icons.LoadAsync(usage);
            if (icon is null) continue;

            row.IconPlateColor = icon.PlateColor;
            row.IconPlate = Palette.IconPlate(theme, icon.PlateColor);
            // The unplated artwork we prefer is drawn edge-to-edge, so it needs a little room
            // inside the tile; desktop icons carry no padding at all and need more.
            row.IconSize = icon.PlateColor is null ? 20 : 22;
            row.Icon = icon.Image;
        }
    }

    /// <summary>Plates are theme-dependent, so they are re-picked when the theme changes.</summary>
    private void RepaintIconPlates()
    {
        var theme = PageRoot.ActualTheme;
        foreach (var row in ViewModel.Apps)
            if (row.Icon is not null)
                row.IconPlate = Palette.IconPlate(theme, row.IconPlateColor);
    }

    private void OnShowAllApps(object sender, RoutedEventArgs e) => ViewModel.ShowAllApps();

    // ---- skeleton shimmer ----------------------------------------------------

    /// <summary>Widths of the name and bar placeholders, per row — uneven, so it reads as content.</summary>
    private static readonly (double Name, double Bar)[] SkeletonShape =
    {
        (150, 620), (112, 500), (176, 380), (132, 260)
    };

    private const double SheenWidth = 110;
    private static readonly TimeSpan SheenDuration = TimeSpan.FromMilliseconds(1250);

    /// <summary>
    /// Placeholder rows for the app list. Each bar carries a sheen that sweeps across it,
    /// staggered down the list so the whole block moves as one wave rather than blinking.
    /// </summary>
    private void BuildSkeleton()
    {
        _shimmer.Stop();
        _shimmer.Children.Clear();
        AppSkeleton.Children.Clear();

        var theme = PageRoot.ActualTheme;

        for (var row = 0; row < SkeletonShape.Length; row++)
        {
            var (nameWidth, barWidth) = SkeletonShape[row];
            var delay = TimeSpan.FromMilliseconds(row * 140);

            // Shapes mirror a real row: the icon plate, the app name, then the usage bar.
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            head.Children.Add(SkeletonBlock(28, 28, 6, theme, delay));
            head.Children.Add(SkeletonBlock(nameWidth, 12, 4, theme, delay));

            var body = new StackPanel { Spacing = 9 };
            body.Children.Add(head);
            body.Children.Add(SkeletonBlock(barWidth, 6, 3, theme, delay));

            // No fixed height: let the row size to its contents, or the usage bar gets clipped off.
            AppSkeleton.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Palette.CardBackground(theme),
                BorderBrush = Palette.CardStroke(theme),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Child = body
            });
        }
    }

    private FrameworkElement SkeletonBlock(
        double width, double height, double radius, ElementTheme theme, TimeSpan delay)
    {
        var bar = new Rectangle
        {
            Fill = Palette.SkeletonBase(theme),
            RadiusX = radius,
            RadiusY = radius
        };

        var translate = new TranslateTransform { X = -SheenWidth };
        var sheen = new Rectangle
        {
            Fill = Palette.SkeletonSheen(theme),
            Width = SheenWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = translate
        };

        var host = new Grid
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Keeps the sheen from spilling past the bar it belongs to.
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) }
        };
        host.Children.Add(bar);
        host.Children.Add(sheen);

        // TranslateTransform.X is an independent animation, so this runs off the UI thread.
        var sweep = new DoubleAnimation
        {
            From = -SheenWidth,
            To = width + SheenWidth,
            Duration = new Duration(SheenDuration),
            BeginTime = delay,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(sweep, translate);
        Storyboard.SetTargetProperty(sweep, "X");
        _shimmer.Children.Add(sweep);

        return host;
    }

    private void UpdateSkeletonState()
    {
        if (ViewModel.IsLoadingApps)
        {
            BuildSkeleton();
            _shimmer.Begin();
        }
        else
        {
            _shimmer.Stop();
        }
    }

    // ---- proportional visuals ------------------------------------------------

    /// <summary>
    /// Star-sized columns give exact proportions at any window width. A minimum width keeps
    /// a 0.06% network visible instead of collapsing it to nothing.
    /// </summary>
    private void RebuildContributionBar()
    {
        ContributionBar.ColumnDefinitions.Clear();
        ContributionBar.Children.Clear();

        var theme = PageRoot.ActualTheme;
        var segments = ViewModel.Segments;
        if (segments.Count == 0) return;

        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            ContributionBar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(segment.Share, GridUnitType.Star),
                MinWidth = 5
            });

            var first = i == 0;
            var last = i == segments.Count - 1;

            var slice = new Border
            {
                Background = Palette.Network(theme, segment.ColorIndex),
                CornerRadius = new CornerRadius(first ? 4 : 2, last ? 4 : 2, last ? 4 : 2, first ? 4 : 2),
                Margin = new Thickness(first ? 0 : 1.5, 0, last ? 0 : 1.5, 0)
                // No dimming: the view model only supplies the segments the total covers, so a
                // selection already fills the bar.
            };

            ToolTipService.SetToolTip(slice, $"{segment.Name} · {segment.ValueText} · {segment.PercentText}");
            Grid.SetColumn(slice, i);
            ContributionBar.Children.Add(slice);
        }
    }

    /// <summary>
    /// The Storage overview pattern: one tile per network under the bar, colour-matched to its
    /// segment. Tiles wrap onto further rows rather than shrinking past readability, and each
    /// one is a button that drills the whole page into that network.
    /// </summary>
    private void RebuildNetworkChips()
    {
        NetworkChips.ColumnDefinitions.Clear();
        NetworkChips.RowDefinitions.Clear();
        NetworkChips.Children.Clear();

        var theme = PageRoot.ActualTheme;
        var networks = ViewModel.Networks;
        if (networks.Count == 0) return;

        const double MinChipWidth = 176;
        var available = NetworkChips.ActualWidth > 0 ? NetworkChips.ActualWidth : 880;
        var columns = Math.Max(1, Math.Min(networks.Count, (int)(available / MinChipWidth)));
        var rows = (int)Math.Ceiling(networks.Count / (double)columns);

        for (int c = 0; c < columns; c++)
            NetworkChips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++)
            NetworkChips.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < networks.Count; i++)
        {
            var n = networks[i];
            var accent = Palette.Network(theme, n.ColorIndex);

            var dot = new Rectangle
            {
                Width = 8,
                Height = 8,
                RadiusX = 2,
                RadiusY = 2,
                Fill = accent,
                VerticalAlignment = VerticalAlignment.Center
            };

            var name = new TextBlock
            {
                Text = n.Name,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Palette.TextPrimary(theme)
            };

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(dot);
            titleRow.Children.Add(name);

            var value = new TextBlock
            {
                Text = $"{n.ValueText}   {n.PercentText}",
                FontSize = 12,
                Margin = new Thickness(16, 4, 0, 0),
                Foreground = Palette.TextTertiary(theme)
            };

            var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            text.Children.Add(titleRow);
            text.Children.Add(value);

            // A chevron marks the tile as something you can act on; once it is part of the
            // selection the tick says so, in the network's own colour.
            var marker = new FontIcon
            {
                Glyph = char.ConvertFromUtf32(n.IsSelected ? 0xE73E : 0xE76C),   // CheckMark / ChevronRight
                FontSize = n.IsSelected ? 14 : 12,
                Foreground = n.IsSelected ? accent : Palette.TextTertiary(theme),
                VerticalAlignment = VerticalAlignment.Center
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(marker, 1);
            layout.Children.Add(text);
            layout.Children.Add(marker);

            var chip = new Button
            {
                Content = layout,
                Tag = n.Id,
                Background = Palette.CardBackground(theme),
                BorderBrush = n.IsSelected ? accent : Palette.CardStroke(theme),
                BorderThickness = new Thickness(n.IsSelected ? 2 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 10, 10),
                Margin = new Thickness(i % columns == 0 ? 0 : 4, i < columns ? 0 : 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            ToolTipService.SetToolTip(chip, n.IsSelected
                ? $"{n.Name} — click to remove from the selection"
                : $"{n.Name} — click to add to the selection");

            chip.Click += (s, _) => ViewModel.ToggleNetwork((long)((Button)s).Tag);

            Grid.SetColumn(chip, i % columns);
            Grid.SetRow(chip, i / columns);
            NetworkChips.Children.Add(chip);
        }
    }

    private void RebuildChart()
    {
        ChartHost.ColumnDefinitions.Clear();
        ChartHost.Children.Clear();

        var theme = PageRoot.ActualTheme;
        var bars = ViewModel.Chart;
        if (bars.Count == 0) return;

        var fill = Palette.Chart(theme);
        var chartHeight = ChartHost.ActualHeight > 0 ? ChartHost.ActualHeight : ChartHost.Height;

        for (int i = 0; i < bars.Count; i++)
        {
            ChartHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bar = new Rectangle
            {
                Fill = fill,
                RadiusX = 2,
                RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = Math.Max(2, bars[i].Ratio * chartHeight),
                Margin = new Thickness(1.5, 0, 1.5, 0),
                // The newest bucket is the one you came to look at; the rest recede.
                Opacity = i == bars.Count - 1 ? 1.0 : 0.7
            };

            ToolTipService.SetToolTip(bar, bars[i].Label);
            Grid.SetColumn(bar, i);
            ChartHost.Children.Add(bar);
        }
    }
}
