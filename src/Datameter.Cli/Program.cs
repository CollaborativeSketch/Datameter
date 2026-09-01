using System.Diagnostics;
using Datameter.Core;

// Headless harness for the data layer. Acceptance test: the 30-day total must land on the
// figure measured directly against the Windows API (167.61 GB on 1 Sep 2026).

Console.OutputEncoding = System.Text.Encoding.UTF8;

var dbPath = args.FirstOrDefault(a => a.StartsWith("--db="))?[5..] ?? UsageStore.DefaultPath;
var full = args.Contains("--full");
var doImport = args.Contains("--import");

Console.WriteLine($"database  {dbPath}");
Console.WriteLine();

using var store = new UsageStore(dbPath);
var provider = new UsageProvider();
var sync = new SyncService(provider, store);

// ---- sync ----------------------------------------------------------------
var sw = Stopwatch.StartNew();
var progress = new Progress<SyncProgress>(p =>
    Console.WriteLine($"  [{p.Index,2}/{p.Total}] {Trim(p.ProfileName, 34),-34} {ByteFormat.Humanize(p.BytesAdded),12}"));

Console.WriteLine(full ? "Full sweep of every remembered profile..." : "Delta sync of known-active networks...");
await sync.SyncAsync(full, progress);
sw.Stop();
Console.WriteLine($"  sync completed in {sw.Elapsed.TotalSeconds:N1}s");
Console.WriteLine();

// ---- optional archive import --------------------------------------------
if (doImport)
{
    if (!ArchiveImporter.ArchiveExists())
    {
        Console.WriteLine("No DataUsage archive found; skipping import.");
    }
    else
    {
        Console.WriteLine("Importing DataUsage archive...");
        var importer = new ArchiveImporter(store);
        var r = importer.Import();
        Console.WriteLine($"  {r.FilesRead} files, {r.NetworksMatched} matched, {r.NetworksCreated} created");
        Console.WriteLine($"  {r.HoursImported} new hours, {ByteFormat.Humanize(r.BytesImported)} seen");
        if (r.Earliest is not null)
            Console.WriteLine($"  archive spans {r.Earliest:yyyy-MM-dd} .. {r.Latest:yyyy-MM-dd}");
    }
    Console.WriteLine();
}

// ---- report --------------------------------------------------------------
var now = DateTimeOffset.UtcNow;
var earliest = store.GetEarliestRecordedHour();
Console.WriteLine($"records begin {earliest:yyyy-MM-dd HH:mm} UTC");
Console.WriteLine();

foreach (var (label, span) in new (string, TimeSpan)[]
{
    ("Last 24 hours", TimeSpan.FromHours(24)),
    ("Last 7 days",   TimeSpan.FromDays(7)),
    ("Last 30 days",  TimeSpan.FromDays(30)),
    ("Last 90 days",  TimeSpan.FromDays(90)),
    ("Last 365 days", TimeSpan.FromDays(365)),
})
{
    var summary = store.GetSummary(UsageProvider.FloorToHour(now - span), now.AddHours(1));
    var active = summary.ActiveNetworks;

    Console.WriteLine($"=== {label} ===");
    Console.WriteLine($"  TOTAL {ByteFormat.Humanize(summary.Total),12}   " +
                      $"(sent {ByteFormat.Humanize(summary.BytesSent)}, received {ByteFormat.Humanize(summary.BytesReceived)})");

    foreach (var n in active)
    {
        var pct = summary.Total > 0 ? 100d * n.Total / summary.Total : 0;
        Console.WriteLine($"    {Trim(n.ProfileName, 32),-32} {ByteFormat.Humanize(n.Total),10}  {pct,5:N1}%  {n.Kind}");
    }

    if (active.Count == 0) Console.WriteLine("    (no usage recorded)");
    Console.WriteLine();
}

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
