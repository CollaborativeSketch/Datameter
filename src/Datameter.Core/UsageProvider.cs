using Windows.Networking.Connectivity;

namespace Datameter.Core;

/// <summary>A connection profile paired with the identity we store it under.</summary>
public sealed class ProfileHandle
{
    public required ConnectionProfile Profile { get; init; }
    public required string ProfileName { get; init; }
    public string? AdapterId { get; init; }
    /// <summary>Null when the profile is not available, so nothing is known rather than "Other".</summary>
    public NetworkKind? Kind { get; init; }

    /// <summary>Null when the profile is not available, so nothing is known rather than "no".</summary>
    public bool? IsMetered { get; init; }
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
    /// <returns>
    /// The buckets, or <c>null</c> if the read failed. Null and empty mean different things: an
    /// empty list is "this network carried nothing", null is "we do not know". Collapsing the
    /// two is how a swallowed exception used to seal a permanent hole in the history, because
    /// the caller advanced its cursor past hours it had never actually read.
    /// </returns>
    public async Task<IReadOnlyList<UsageBucket>?> GetHourlyAsync(
        ProfileHandle handle,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        var start = FloorToHour(fromUtc.ToUniversalTime());
        var end = toUtc.ToUniversalTime();

        if (end <= start) return Array.Empty<UsageBucket>();

        // Nothing was asked of Windows, so nothing can have failed.

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
            // A profile can vanish between enumeration and query, or refuse the range. One bad
            // network must never take down the whole sync — but it must not be mistaken for a
            // quiet one either, so the caller is told the read failed rather than handed zero.
            return null;
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

    /// <summary>
    /// The profile Windows is currently using for the internet, if any.
    ///
    /// A network joined today has no stored hours, so it is invisible to any filter based on
    /// what has already been recorded. This is the one profile that is certainly worth reading.
    /// </summary>
    public static string? ConnectedProfileName()
    {
        try
        {
            return NetworkInformation.GetInternetConnectionProfile()?.ProfileName;
        }
        catch
        {
            return null;
        }
    }

    public static DateTimeOffset FloorToHour(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);

    private static string? TryGetAdapterId(ConnectionProfile profile)
    {
        try { return profile.NetworkAdapter?.NetworkAdapterId.ToString(); }
        catch { return null; }
    }

    /// <summary>Null rather than Other when the profile cannot be asked.</summary>
    private static NetworkKind? ClassifyKind(ConnectionProfile profile)
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
                null => null,
                _ => NetworkKind.Other
            };
        }
        catch
        {
            // NetworkAdapter throws for any profile that is not currently available, which is
            // most of them most of the time. That is not evidence of anything.
            return null;
        }
    }

    /// <summary>Null rather than false when the cost cannot be asked.</summary>
    private static bool? TryIsMetered(ConnectionProfile profile)
    {
        try
        {
            var cost = profile.GetConnectionCost();

            // Unknown is a real answer from Windows, and it is not "no".
            if (cost.NetworkCostType == NetworkCostType.Unknown) return null;

            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
        }
        catch
        {
            return null;
        }
    }
}
