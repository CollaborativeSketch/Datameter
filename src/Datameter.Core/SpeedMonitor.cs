using System.Diagnostics;
using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;

namespace Datameter.Core;

/// <summary>Throughput over the interval that just elapsed, in bytes per second.</summary>
public readonly record struct SpeedSample(long SentPerSecond, long ReceivedPerSecond, string? InterfaceName)
{
    public long TotalPerSecond => SentPerSecond + ReceivedPerSecond;

    public static SpeedSample Idle => new(0, 0, null);
}

/// <summary>
/// Live throughput, sampled from the adapter's own byte counters.
///
/// This is a different source from the rest of the app. <c>GetNetworkUsageAsync</c> reports
/// history in whole hours and cannot answer "how fast, right now", so the meter reads
/// <see cref="NetworkInterface"/> statistics and differentiates them instead. Nothing here is
/// recorded: the counters reset when the adapter does, so they are worth a reading and not a row.
///
/// Callers own the cadence. Each <see cref="Sample"/> returns the rate since the previous call.
/// </summary>
public sealed class SpeedMonitor
{
    /// <summary>
    /// How long to trust the cached choice of adapter. Resolving it walks the connection
    /// profiles, which is far dearer than reading a counter, and the answer only changes when
    /// the machine moves between networks.
    /// </summary>
    private static readonly TimeSpan AdapterCacheLife = TimeSpan.FromSeconds(5);

    private long _lastSent;
    private long _lastReceived;
    private long _lastTimestamp;
    private string? _lastAdapterId;

    private string? _preferredId;
    private long _preferredResolvedAt;

    /// <summary>
    /// Rate since the previous call. The first call establishes the baseline and reports
    /// nothing, because a rate needs two readings.
    /// </summary>
    public SpeedSample Sample()
    {
        var reading = Read();
        var now = Stopwatch.GetTimestamp();

        // A first call, or a different adapter, means the counters are not comparable with what
        // we hold. Re-baseline and report nothing rather than a fictitious spike.
        var comparable = _lastTimestamp != 0 && reading.AdapterId == _lastAdapterId;

        var sentDelta = reading.Sent - _lastSent;
        var receivedDelta = reading.Received - _lastReceived;

        var seconds = comparable
            ? (now - _lastTimestamp) / (double)Stopwatch.Frequency
            : 0;

        _lastSent = reading.Sent;
        _lastReceived = reading.Received;
        _lastTimestamp = now;
        _lastAdapterId = reading.AdapterId;

        // Counters are cumulative and can only go backwards by being reset under us.
        if (!comparable || seconds <= 0 || sentDelta < 0 || receivedDelta < 0)
            return new SpeedSample(0, 0, reading.Name);

        return new SpeedSample(
            (long)(sentDelta / seconds),
            (long)(receivedDelta / seconds),
            reading.Name);
    }

    /// <summary>Forgets the baseline, so the next sample starts a fresh interval.</summary>
    public void Reset()
    {
        _lastTimestamp = 0;
        _lastAdapterId = null;
    }

    private readonly record struct Reading(long Sent, long Received, string? Name, string? AdapterId);

    /// <summary>
    /// Byte counters for the adapter carrying the internet connection.
    ///
    /// Preferring that one adapter is what keeps the figure honest. Summing every adapter that
    /// happens to be up double-counts the moment a VPN or a virtual switch is in play, because
    /// the same bytes cross the tunnel and the physical card underneath it.
    /// </summary>
    private Reading Read()
    {
        var preferred = PreferredAdapterId();

        long sent = 0, received = 0;
        string? name = null;
        string? adapterId = null;
        var matched = false;

        foreach (var nic in SafeInterfaces())
        {
            var isPreferred = preferred is not null && IdMatches(nic.Id, preferred);
            if (preferred is not null && !isPreferred) continue;

            if (!isPreferred && !IsCandidate(nic)) continue;

            long nicSent, nicReceived;
            try
            {
                var stats = nic.GetIPStatistics();
                nicSent = stats.BytesSent;
                nicReceived = stats.BytesReceived;
            }
            catch
            {
                // Some virtual adapters refuse statistics. One of them must not blank the meter.
                continue;
            }

            sent += nicSent;
            received += nicReceived;
            name ??= nic.Name;
            adapterId = matched ? "sum" : nic.Id;
            matched = true;
        }

        // The preferred adapter can disappear between resolving it and reading it. Fall back to
        // the summed candidates rather than reporting a flat zero.
        if (!matched && preferred is not null)
        {
            _preferredId = null;
            _preferredResolvedAt = 0;
            return ReadAllCandidates();
        }

        return new Reading(sent, received, name, adapterId);
    }

    private Reading ReadAllCandidates()
    {
        long sent = 0, received = 0;
        string? name = null;

        foreach (var nic in SafeInterfaces())
        {
            if (!IsCandidate(nic)) continue;

            try
            {
                var stats = nic.GetIPStatistics();
                sent += stats.BytesSent;
                received += stats.BytesReceived;
                name ??= nic.Name;
            }
            catch
            {
            }
        }

        return new Reading(sent, received, name, "sum");
    }

    private static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return Array.Empty<NetworkInterface>();
        }
    }

    private static bool IsCandidate(NetworkInterface nic) =>
        nic.OperationalStatus == OperationalStatus.Up &&
        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel;

    /// <summary>
    /// The adapter behind the current internet connection, as a NetworkInterface id.
    ///
    /// <c>ConnectionProfile.NetworkAdapter</c> throws for any profile that is not available,
    /// which is exactly why network identity elsewhere in this app is the profile name. Here the
    /// profile is by definition the connected one, so the adapter is readable — but it is still
    /// guarded, because the connection can drop mid-call.
    /// </summary>
    private string? PreferredAdapterId()
    {
        var now = Stopwatch.GetTimestamp();
        var age = _preferredResolvedAt == 0
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds((now - _preferredResolvedAt) / (double)Stopwatch.Frequency);

        if (age < AdapterCacheLife) return _preferredId;

        _preferredResolvedAt = now;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            _preferredId = profile?.NetworkAdapter?.NetworkAdapterId.ToString();
        }
        catch
        {
            _preferredId = null;
        }

        return _preferredId;
    }

    /// <summary>
    /// NetworkInterface ids carry braces, WinRT adapter ids do not, and neither agrees on case.
    /// </summary>
    private static bool IdMatches(string interfaceId, string adapterId) =>
        Trim(interfaceId).Equals(Trim(adapterId), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string id) => id.Trim('{', '}');
}
