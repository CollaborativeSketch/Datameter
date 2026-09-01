using Windows.Networking.Connectivity;

namespace Datameter.Core;

/// <summary>A connection profile paired with the identity we store it under.</summary>
public sealed class ProfileHandle
{
    public required ConnectionProfile Profile { get; init; }
    public required string ProfileName { get; init; }
    public string? AdapterId { get; init; }
    public NetworkKind Kind { get; init; }
    public bool IsMetered { get; init; }
}

/// <summary>
/// Reads network usage out of Windows. Everything here is bounded by two measured limits:
/// a query span may not exceed ~58 days, and a call costs ~3.1s for a 30-day span regardless
/// of granularity — so we always ask for hourly buckets and roll them up ourselves.
/// </summary>
public sealed class UsageProvider
{
    /// <summary>Measured ceiling is between 58 and 60 days; stay clear of it.</summary>
    public static readonly TimeSpan MaxQuerySpan = TimeSpan.FromDays(56);

    public IReadOnlyList<ProfileHandle> EnumerateProfiles()
    {
        var handles = new List<ProfileHandle>();

        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            if (profile is null) continue;

            var name = profile.ProfileName;
            if (string.IsNullOrWhiteSpace(name)) continue;

            handles.Add(new ProfileHandle
            {
                Profile = profile,
                ProfileName = name,
                AdapterId = TryGetAdapterId(profile),
                Kind = ClassifyKind(profile),
                IsMetered = TryIsMetered(profile)
            });
        }

        return handles;
    }

    /// <summary>
    /// Hourly usage for one profile. The API returns buckets in chronological order with no
    /// timestamps of their own, so bucket i is <paramref name="fromUtc"/> + i hours — which is
    /// only true if the start is aligned to an exact hour. We align it here rather than trusting
    /// the caller.
    /// </summary>
    public async Task<IReadOnlyList<UsageBucket>> GetHourlyAsync(
        ProfileHandle handle,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        var start = FloorToHour(fromUtc.ToUniversalTime());
        var end = toUtc.ToUniversalTime();

        if (end <= start) return Array.Empty<UsageBucket>();

        // Asking beyond the ceiling throws E_INVALIDARG for PerHour, and — worse — silently
        // returns zero buckets for Total. Clamp instead of catching.
        if (end - start > MaxQuerySpan)
            start = FloorToHour(end - MaxQuerySpan);

        IReadOnlyList<NetworkUsage> raw;
        try
        {
            raw = await handle.Profile
                .GetNetworkUsageAsync(start, end, DataUsageGranularity.PerHour, new NetworkUsageStates())
                .AsTask(ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A profile can vanish between enumeration and query, or refuse the range.
            // One bad network must never take down the whole sync.
            return Array.Empty<UsageBucket>();
        }

        var buckets = new List<UsageBucket>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var u = raw[i];
            if (u.BytesSent == 0 && u.BytesReceived == 0) continue;   // don't store empty hours
            buckets.Add(new UsageBucket(start.AddHours(i), (long)u.BytesSent, (long)u.BytesReceived));
        }

        return buckets;
    }

    public static DateTimeOffset FloorToHour(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);

    private static string? TryGetAdapterId(ConnectionProfile profile)
    {
        try { return profile.NetworkAdapter?.NetworkAdapterId.ToString(); }
        catch { return null; }
    }

    private static NetworkKind ClassifyKind(ConnectionProfile profile)
    {
        try
        {
            if (profile.IsWlanConnectionProfile) return NetworkKind.WiFi;
            if (profile.IsWwanConnectionProfile) return NetworkKind.Cellular;

            // IANA interface types: 6 = ethernetCsmacd, 71 = ieee80211.
            var iana = profile.NetworkAdapter?.IanaInterfaceType;
            return iana switch
            {
                6 => NetworkKind.Ethernet,
                71 => NetworkKind.WiFi,
                243 or 244 => NetworkKind.Cellular,
                _ => NetworkKind.Other
            };
        }
        catch
        {
            return NetworkKind.Other;
        }
    }

    private static bool TryIsMetered(ConnectionProfile profile)
    {
        try
        {
            var cost = profile.GetConnectionCost();
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
        }
        catch
        {
            return false;
        }
    }
}
