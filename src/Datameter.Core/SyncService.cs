namespace Datameter.Core;

public sealed record SyncProgress(string ProfileName, int Index, int Total, long BytesAdded);

/// <summary>
/// What one sync achieved. Failures are counted rather than swallowed, so the caller can say
/// that a network could not be read instead of clearing the status line as though all were well.
/// </summary>
public sealed record SyncResult(long BytesAdded, int NetworksRead, int NetworksFailed);

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
    public async Task<SyncResult> SyncAsync(
        bool full,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var handles = _provider.EnumerateProfiles();

        // Every profile is recorded before anything is filtered. A network joined today has no
        // stored hours, so a filter based on what has already been recorded drops it before it
        // is ever written down — and it stays dropped for good. Upserting first means a hotel
        // Wi-Fi, a new hotspot or a new SIM at least exists to be read.
        var productive = full ? null : _store.GetProductiveKeys();
        var connected = full ? null : UsageProvider.ConnectedProfileName();

        long totalAdded = 0;
        var read = 0;
        var failed = 0;

        for (int i = 0; i < handles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var handle = handles[i];
            var networkId = _store.UpsertNetwork(handle);
            var last = _store.GetLastSyncedHour(networkId);

            // Skip only what is known to be unproductive. A network never read before, and the
            // one the machine is on right now, are always worth the query.
            if (productive is { Count: > 0 }
                && last is not null
                && !productive.Contains(handle.ProfileName)
                && handle.ProfileName != connected)
            {
                continue;
            }

            var from = last is null
                ? now - UsageProvider.MaxQuerySpan          // first sight: reach as far back as allowed
                : last.Value - RefetchTail;

            var buckets = await _provider.GetHourlyAsync(handle, from, now, ct).ConfigureAwait(false);

            if (buckets is null)
            {
                // The read failed. Leaving the cursor where it is means these hours are tried
                // again; advancing it would seal a hole that nothing ever reopens.
                failed++;
                progress?.Report(new SyncProgress(handle.ProfileName, i + 1, handles.Count, 0));
                continue;
            }

            if (buckets.Count > 0)
            {
                _store.WriteBuckets(networkId, buckets);
                totalAdded += buckets.Sum(b => b.Total);
            }

            _store.SetLastSyncedHour(networkId, UsageProvider.FloorToHour(now));
            read++;

            progress?.Report(new SyncProgress(
                handle.ProfileName, i + 1, handles.Count, buckets.Sum(b => b.Total)));
        }

        return new SyncResult(totalAdded, read, failed);
    }
}
