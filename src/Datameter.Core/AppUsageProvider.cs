using Windows.Networking.Connectivity;
using Windows.Storage.Streams;

namespace Datameter.Core;

/// <summary>Bytes moved by one application, summed across every network in the window.</summary>
public sealed record AppUsage(
    string Name,
    string AttributionId,
    long BytesSent,
    long BytesReceived,
    IRandomAccessStreamReference? Thumbnail)
{
    public long Total => BytesSent + BytesReceived;
}

/// <summary>
/// Per-application usage, the same breakdown the Settings page shows.
///
/// Unlike <see cref="UsageProvider"/> this cannot be cached by hour: the API returns totals
/// for a range with no per-hour attribution, so every window has to be asked for separately
/// and nothing can be accumulated beyond what Windows itself retains (~30 days).
/// </summary>
public sealed class AppUsageProvider
{
    public async Task<IReadOnlyList<AppUsage>> GetAsync(
        IReadOnlyList<ProfileHandle> handles,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        var start = fromUtc.ToUniversalTime();
        var end = toUtc.ToUniversalTime();
        if (end <= start) return Array.Empty<AppUsage>();

        if (end - start > UsageProvider.MaxQuerySpan)
            start = end - UsageProvider.MaxQuerySpan;

        var totals = new Dictionary<string, (long Sent, long Received, string Name, IRandomAccessStreamReference? Thumb)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var handle in handles)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<AttributedNetworkUsage> usage;
            try
            {
                usage = await handle.Profile
                    .GetAttributedNetworkUsageAsync(start, end, new NetworkUsageStates())
                    .AsTask(ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;   // one uncooperative profile must not sink the whole list
            }

            foreach (var u in usage)
            {
                var id = u.AttributionId ?? string.Empty;
                var name = string.IsNullOrWhiteSpace(u.AttributionName)
                    ? FriendlyName(id)
                    : u.AttributionName;

                totals.TryGetValue(id, out var acc);
                totals[id] = (
                    acc.Sent + (long)u.BytesSent,
                    acc.Received + (long)u.BytesReceived,
                    acc.Name ?? name,
                    acc.Thumb ?? u.AttributionThumbnail);
            }
        }

        return totals
            .Select(kv => new AppUsage(kv.Value.Name, kv.Key, kv.Value.Sent, kv.Value.Received, kv.Value.Thumb))
            .Where(a => a.Total > 0)
            .OrderByDescending(a => a.Total)
            .ToList();
    }

    /// <summary>
    /// Windows hands back either an NT device path or a package family name, and usually no
    /// display name at all, so we derive something readable — matching what Settings shows.
    /// </summary>
    public static string FriendlyName(string attributionId)
    {
        if (string.IsNullOrWhiteSpace(attributionId)) return "System";

        // "\device\harddiskvolume3\program files\google\chrome\application\chrome.exe"
        var slash = attributionId.LastIndexOf('\\');
        if (slash >= 0)
        {
            var leaf = attributionId[(slash + 1)..];
            return string.IsNullOrWhiteSpace(leaf) ? "System" : leaf;
        }

        // "MSTeams_8wekyb3d8bbwe" — a package family name; the publisher hash carries no meaning.
        var underscore = attributionId.LastIndexOf('_');
        var family = underscore > 0 ? attributionId[..underscore] : attributionId;

        // "OpenAI.Codex" reads better as "Codex"; keep a single-segment name as-is.
        var dot = family.LastIndexOf('.');
        return dot > 0 && dot < family.Length - 1 ? family[(dot + 1)..] : family;
    }
}
