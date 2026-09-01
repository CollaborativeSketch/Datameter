using System.Text.Json;

namespace Datameter.Core;

public sealed record ImportResult(
    int FilesRead,
    int NetworksMatched,
    int NetworksCreated,
    int HoursImported,
    long BytesImported,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest);

/// <summary>
/// Seeds history from the "Data usage" Store app by 31229smartApps, which keeps one JSON file
/// per network under its package LocalState folder. Its records reach back roughly a year —
/// far past the ~30 days Windows itself retains — and include networks whose Wi-Fi profiles
/// have since been deleted, which the usage API can no longer report at all.
///
/// Imported hours never overwrite hours read from the Windows API.
/// </summary>
public sealed class ArchiveImporter
{
    private const string PackageFamily = "31229smartApps.DataUsage_qtjv23y2shy8a";

    /// <summary>The archive writes lowercase keys ("name", "data", "d", "s", "r").</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly UsageStore _store;

    public ArchiveImporter(UsageStore store) => _store = store;

    public static string DefaultArchivePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", PackageFamily, "LocalState");

    public static bool ArchiveExists(string? path = null) =>
        Directory.Exists(path ?? DefaultArchivePath);

    public ImportResult Import(string? archivePath = null)
    {
        var dir = archivePath ?? DefaultArchivePath;
        if (!Directory.Exists(dir))
            return new ImportResult(0, 0, 0, 0, 0, null, null);

        int files = 0, matched = 0, created = 0, hours = 0;
        long bytes = 0;
        DateTimeOffset? earliest = null, latest = null;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            ArchiveFile? parsed;
            try
            {
                using var stream = File.OpenRead(file);
                parsed = JsonSerializer.Deserialize<ArchiveFile>(stream, JsonOptions);
            }
            catch
            {
                continue;   // a malformed file must not abort the import
            }

            if (parsed?.Name is null || parsed.Data is null || parsed.Data.Length == 0) continue;
            files++;

            var buckets = new List<UsageBucket>(parsed.Data.Length);
            foreach (var e in parsed.Data)
            {
                if (e.D <= 0) continue;
                var hour = UsageProvider.FloorToHour(DateTimeOffset.FromUnixTimeMilliseconds(e.D));
                buckets.Add(new UsageBucket(hour, e.S, e.R));

                if (earliest is null || hour < earliest) earliest = hour;
                if (latest is null || hour > latest) latest = hour;
            }

            if (buckets.Count == 0) continue;

            // The archive carries no adapter id, so match on name and fall back to creating a row.
            var existing = _store.FindNetworkByName(parsed.Name);
            long networkId;
            if (existing is not null)
            {
                networkId = existing.Value;
                matched++;
            }
            else
            {
                // This row exists only to hold history; there is no live profile behind it.
                networkId = _store.UpsertNetwork(parsed.Name, adapterId: null, GuessKind(parsed.Name), isMetered: false);
                created++;
            }

            var written = _store.ImportBuckets(networkId, buckets);
            hours += written;
            bytes += buckets.Sum(b => b.Total);
        }

        return new ImportResult(files, matched, created, hours, bytes, earliest, latest);
    }

    private static NetworkKind GuessKind(string name) =>
        name.Equals("Ethernet", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Local Area Connection", StringComparison.OrdinalIgnoreCase)
            ? NetworkKind.Ethernet
            : NetworkKind.WiFi;

    private sealed class ArchiveFile
    {
        public string? Name { get; set; }
        public Entry[]? Data { get; set; }

        public sealed class Entry
        {
            public long D { get; set; }   // epoch milliseconds, start of hour
            public long S { get; set; }   // bytes sent
            public long R { get; set; }   // bytes received
        }
    }
}
