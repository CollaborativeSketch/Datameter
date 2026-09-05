using System.Diagnostics;
using Datameter.Core;

// Headless harness for the data layer: syncs, then prints the same totals the app shows.
// Use it to check figures against the Windows Settings page without involving the UI, and
// --networks to list stored networks and flag any duplicate names.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var dbPath = args.FirstOrDefault(a => a.StartsWith("--db="))?[5..] ?? UsageStore.DefaultPath;
var full = args.Contains("--full");
var doImport = args.Contains("--import");

Console.WriteLine($"database  {dbPath}");
Console.WriteLine();

using var store = new UsageStore(dbPath);
var provider = new UsageProvider();
var sync = new SyncService(provider, store);

// ---- diagnostics ---------------------------------------------------------
// --speed samples the live meter's own source for a while and reports what it saw, plus the
// total it accounts for. Run a transfer of known size alongside it and the totals should agree
// to within the framing overhead the payload does not include.
if (args.Contains("--speed"))
{
    var seconds = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--seconds="))?[10..], out var s) ? s : 15;
    var monitor = new SpeedMonitor();

    Console.WriteLine($"sampling for {seconds}s on the adapter carrying the internet connection");
    Console.WriteLine();

    monitor.Sample();   // the first call only sets the baseline

    // Rates are multiplied back out by the interval that actually elapsed. Summing them as if
    // every interval were exactly a second understates the total by however much Thread.Sleep
    // overshoots, which is a few percent and would look like the meter reading low.
    double sent = 0, received = 0;
    var clock = Stopwatch.StartNew();

    for (var i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);

        var elapsed = clock.Elapsed.TotalSeconds;
        clock.Restart();

        var sample = monitor.Sample();

        sent += sample.SentPerSecond * elapsed;
        received += sample.ReceivedPerSecond * elapsed;

        Console.WriteLine(
            $"{i + 1,3}s  up {ByteFormat.HumanizeRate(sample.SentPerSecond, SpeedUnit.Kilobytes),12}" +
            $"  down {ByteFormat.HumanizeRate(sample.ReceivedPerSecond, SpeedUnit.Kilobytes),12}" +
            $"  ({sample.InterfaceName})");
    }

    Console.WriteLine();
    Console.WriteLine($"accounted for: sent {ByteFormat.Humanize((long)sent)}, received {ByteFormat.Humanize((long)received)}");
    Console.WriteLine($"             : {(long)sent:N0} and {(long)received:N0} bytes");
    return;
}

// --networks prints the stored network rows and stops. Two rows sharing a profile name
// means the same network is being counted twice.
if (args.Contains("--networks"))
{
    var rows = store.DescribeNetworks();
    Console.WriteLine($"{"id",4}  {"profile",-34}  {"adapter",-38}  {"src",3}  {"hours",6}  usage");
    foreach (var (id, name, adapter, source, hours, bytes) in rows)
    {
        var src = source switch { 0 => "win", 1 => "imp", _ => "-" };
        Console.WriteLine($"{id,4}  {Trim(name, 34),-34}  {(adapter.Length == 0 ? "(none)" : adapter),-38}  {src,3}  {hours,6}  {ByteFormat.Humanize(bytes)}");
    }

    var dupes = rows.GroupBy(r => r.ProfileName).Where(g => g.Count() > 1).ToList();
    Console.WriteLine();
    Console.WriteLine($"{rows.Count} network rows, {dupes.Count} profile name(s) appearing more than once");
    foreach (var d in dupes)
        Console.WriteLine($"  DUPLICATE: {d.Key} -> ids {string.Join(", ", d.Select(x => x.Id))}");
    return;
}

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
