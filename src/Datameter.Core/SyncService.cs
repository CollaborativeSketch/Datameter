namespace Datameter.Core;

public sealed record SyncProgress(string ProfileName, int Index, int Total, long BytesAdded);

/// <summary>
/// Keeps the cache current. A full sweep of every remembered profile costs ~93 seconds on a
/// real machine, so that path runs once; afterwards only networks that have ever moved a byte
/// are asked, and only for the hours since they were last read.
/// </summary>
public sealed class SyncService
{
    /// <summary>
    /// The newest hour is still filling when we read it, and Windows can attribute bytes a little
    /// late, so a routine sync always re-reads a short trailing window.
    /// </summary>
    private static readonly TimeSpan RefetchTail = TimeSpan.FromHours(3);

    private readonly UsageProvider _provider;
    private readonly UsageStore _store;

    public SyncService(UsageProvider provider, UsageStore store)
    {
        _provider = provider;
        _store = store;
    }

    /// <summary>
    /// Reads new hours into the cache.
    /// <paramref name="full"/> asks every remembered profile — needed on first run and on
    /// periodic rediscovery. Otherwise only known-productive networks are queried.
    /// </summary>
    public async Task<long> SyncAsync(
        bool full,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var handles = _provider.EnumerateProfiles();

        if (!full)
        {
            var productive = _store.GetProductiveKeys();
            if (productive.Count > 0)
            {
                handles = handles
                    .Where(h => productive.Contains(h.ProfileName))
                    .ToList();
            }
        }

        long totalAdded = 0;

        for (int i = 0; i < handles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var handle = handles[i];
            var networkId = _store.UpsertNetwork(handle);

            var last = _store.GetLastSyncedHour(networkId);
            var from = last is null
                ? now - UsageProvider.MaxQuerySpan          // first sight: reach as far back as allowed
                : last.Value - RefetchTail;

            var buckets = await _provider.GetHourlyAsync(handle, from, now, ct).ConfigureAwait(false);

            if (buckets.Count > 0)
            {
                _store.WriteBuckets(networkId, buckets);
                totalAdded += buckets.Sum(b => b.Total);
            }

            _store.SetLastSyncedHour(networkId, UsageProvider.FloorToHour(now));

            progress?.Report(new SyncProgress(
                handle.ProfileName, i + 1, handles.Count, buckets.Sum(b => b.Total)));
        }

        return totalAdded;
    }
}
