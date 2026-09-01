using Microsoft.Data.Sqlite;

namespace Datameter.Core;

/// <summary>
/// The local cache. Append-only in spirit: once an hour is recorded it survives Windows
/// discarding its own history (~30 days) and survives the Wi-Fi profile being forgotten.
///
/// A SqliteConnection is not thread-safe and this store is read from the UI thread while the
/// sync runs on a worker, so every public member serialises on <see cref="_sync"/>. Locking
/// lives here rather than at the call sites: a caller that forgets corrupts the connection's
/// internal command list, which surfaces later as an unrelated IndexOutOfRange.
/// </summary>
public sealed class UsageStore : IDisposable
{
    private const string HourFormat = "yyyy-MM-dd HH:00";

    private readonly object _sync = new();
    private readonly SqliteConnection _db;

    public UsageStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        CreateSchema();
        MergeDuplicateNetworks();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Datameter", "usage.db");

    private void CreateSchema() => Execute(@"
        CREATE TABLE IF NOT EXISTS Network (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileName   TEXT    NOT NULL,
            AdapterId     TEXT    NOT NULL DEFAULT '',
            Kind          INTEGER NOT NULL,
            IsMetered     INTEGER NOT NULL DEFAULT 0,
            ColorIndex    INTEGER NOT NULL,
            FirstSeenUtc  TEXT    NOT NULL,
            LastSeenUtc   TEXT    NOT NULL,
            UNIQUE (ProfileName, AdapterId)
        );

        CREATE TABLE IF NOT EXISTS HourlyUsage (
            NetworkId     INTEGER NOT NULL REFERENCES Network(Id) ON DELETE CASCADE,
            HourUtc       TEXT    NOT NULL,
            BytesSent     INTEGER NOT NULL,
            BytesReceived INTEGER NOT NULL,
            Source        INTEGER NOT NULL DEFAULT 0,   -- 0 = Windows API, 1 = imported archive
            PRIMARY KEY (NetworkId, HourUtc)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS IX_HourlyUsage_Hour ON HourlyUsage(HourUtc);

        CREATE TABLE IF NOT EXISTS SyncState (
            NetworkId      INTEGER PRIMARY KEY REFERENCES Network(Id) ON DELETE CASCADE,
            LastSyncedHour TEXT NOT NULL,
            LastAttemptUtc TEXT NOT NULL
        );");

    /// <summary>
    /// Collapses networks that share a profile name, then makes the name unique.
    ///
    /// Identity used to be (ProfileName, AdapterId), which looked more precise and was wrong:
    /// ConnectionProfile.NetworkAdapter throws for a profile that is not currently available,
    /// so the adapter id is only known when you happen to be connected to that network. The
    /// same Wi-Fi therefore stored as two rows — one with an adapter id, one without — and the
    /// totals counted it twice. The profile name is the only identifier Windows gives us that
    /// is stable whether or not the network is in range.
    /// </summary>
    private void MergeDuplicateNetworks()
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();

            var duplicates = new List<(long Keep, long Drop)>();
            using (var find = _db.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText =
                    "SELECT a.Id, b.Id FROM Network a JOIN Network b " +
                    "ON a.ProfileName = b.ProfileName AND a.Id < b.Id;";
                using var r = find.ExecuteReader();
                while (r.Read()) duplicates.Add((r.GetInt64(0), r.GetInt64(1)));
            }

            foreach (var (keep, drop) in duplicates)
            {
                using (var move = _db.CreateCommand())
                {
                    move.Transaction = tx;
                    // Hours the API supplied (Source 0) beat imported ones (Source 1) on collision.
                    move.CommandText =
                        "INSERT INTO HourlyUsage (NetworkId, HourUtc, BytesSent, BytesReceived, Source) " +
                        "SELECT $keep, HourUtc, BytesSent, BytesReceived, Source " +
                        "FROM HourlyUsage WHERE NetworkId = $drop " +
                        "ON CONFLICT(NetworkId, HourUtc) DO UPDATE SET " +
                        "  BytesSent = excluded.BytesSent, BytesReceived = excluded.BytesReceived, " +
                        "  Source = excluded.Source " +
                        "WHERE excluded.Source <= HourlyUsage.Source;";
                    move.Parameters.AddWithValue("$keep", keep);
                    move.Parameters.AddWithValue("$drop", drop);
                    move.ExecuteNonQuery();
                }

                using var remove = _db.CreateCommand();
                remove.Transaction = tx;
                remove.CommandText = "DELETE FROM Network WHERE Id = $drop;";   // cascades
                remove.Parameters.AddWithValue("$drop", drop);
                remove.ExecuteNonQuery();
            }

            using (var index = _db.CreateCommand())
            {
                index.Transaction = tx;
                index.CommandText =
                    "CREATE UNIQUE INDEX IF NOT EXISTS UX_Network_ProfileName ON Network(ProfileName);";
                index.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Finds or creates the row for a network, keeping its colour stable forever.</summary>
    public long UpsertNetwork(ProfileHandle handle) =>
        UpsertNetwork(handle.ProfileName, handle.AdapterId, handle.Kind, handle.IsMetered);

    /// <summary>
    /// Identity-only overload, for history recovered from an archive where no live
    /// connection profile exists to hand over.
    /// </summary>
    public long UpsertNetwork(string profileName, string? adapterId, NetworkKind kind, bool isMetered)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            adapterId ??= string.Empty;

            using (var find = _db.CreateCommand())
            {
                // Matched on name alone. The adapter id is only knowable while the network is
                // available, so including it here would create a second row for the same Wi-Fi
                // whenever it happened to be out of range — and double its usage in every total.
                find.CommandText = "SELECT Id FROM Network WHERE ProfileName = $n;";
                find.Parameters.AddWithValue("$n", profileName);

                if (find.ExecuteScalar() is long existing)
                {
                    using var touch = _db.CreateCommand();
                    // Keep a known adapter id rather than letting a later blank overwrite it.
                    touch.CommandText =
                        "UPDATE Network SET LastSeenUtc = $t, IsMetered = $m, Kind = $k, " +
                        "  AdapterId = CASE WHEN $a <> '' THEN $a ELSE AdapterId END " +
                        "WHERE Id = $id;";
                    touch.Parameters.AddWithValue("$t", now);
                    touch.Parameters.AddWithValue("$m", isMetered ? 1 : 0);
                    touch.Parameters.AddWithValue("$k", (int)kind);
                    touch.Parameters.AddWithValue("$a", adapterId);
                    touch.Parameters.AddWithValue("$id", existing);
                    touch.ExecuteNonQuery();
                    return existing;
                }
            }

            // Colour is assigned once, on first sight, and never reshuffles afterwards.
            using var count = _db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Network;";
            var colorIndex = Convert.ToInt32(count.ExecuteScalar());

            using var insert = _db.CreateCommand();
            insert.CommandText =
                "INSERT INTO Network (ProfileName, AdapterId, Kind, IsMetered, ColorIndex, FirstSeenUtc, LastSeenUtc) " +
                "VALUES ($n, $a, $k, $m, $c, $t, $t); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$n", profileName);
            insert.Parameters.AddWithValue("$a", adapterId);
            insert.Parameters.AddWithValue("$k", (int)kind);
            insert.Parameters.AddWithValue("$m", isMetered ? 1 : 0);
            insert.Parameters.AddWithValue("$c", colorIndex);
            insert.Parameters.AddWithValue("$t", now);
            return (long)insert.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Writes hours read from the Windows API. These are authoritative and overwrite whatever
    /// is already there, including anything a previous import put down.
    /// </summary>
    public void WriteBuckets(long networkId, IReadOnlyList<UsageBucket> buckets) =>
        Write(networkId, buckets, source: 0, overwrite: true);

    /// <summary>
    /// Writes hours recovered from an external archive. These never overwrite a row the
    /// Windows API supplied — the API wins wherever both can speak.
    /// </summary>
    public int ImportBuckets(long networkId, IReadOnlyList<UsageBucket> buckets) =>
        Write(networkId, buckets, source: 1, overwrite: false);

    private int Write(long networkId, IReadOnlyList<UsageBucket> buckets, int source, bool overwrite)
    {
        if (buckets.Count == 0) return 0;

        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = overwrite
                ? "INSERT INTO HourlyUsage (NetworkId, HourUtc, BytesSent, BytesReceived, Source) " +
                  "VALUES ($id, $h, $s, $r, $src) " +
                  "ON CONFLICT(NetworkId, HourUtc) DO UPDATE SET " +
                  "  BytesSent = excluded.BytesSent, BytesReceived = excluded.BytesReceived, " +
                  "  Source = excluded.Source;"
                : "INSERT INTO HourlyUsage (NetworkId, HourUtc, BytesSent, BytesReceived, Source) " +
                  "VALUES ($id, $h, $s, $r, $src) " +
                  "ON CONFLICT(NetworkId, HourUtc) DO NOTHING;";

            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pH = cmd.Parameters.Add("$h", SqliteType.Text);
            var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pR = cmd.Parameters.Add("$r", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$src", source);

            var written = 0;
            foreach (var b in buckets)
            {
                pId.Value = networkId;
                pH.Value = b.HourUtc.UtcDateTime.ToString(HourFormat);
                pS.Value = b.BytesSent;
                pR.Value = b.BytesReceived;
                written += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return written;
        }
    }

    /// <summary>Finds a network by name alone — archives carry no adapter id to match on.</summary>
    public long? FindNetworkByName(string profileName)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Network WHERE ProfileName = $n ORDER BY Id LIMIT 1;";
            cmd.Parameters.AddWithValue("$n", profileName);
            return cmd.ExecuteScalar() is long id ? id : null;
        }
    }

    public DateTimeOffset? GetLastSyncedHour(long networkId)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT LastSyncedHour FROM SyncState WHERE NetworkId = $id;";
            cmd.Parameters.AddWithValue("$id", networkId);
            return cmd.ExecuteScalar() is string s ? ParseHour(s) : null;
        }
    }

    public void SetLastSyncedHour(long networkId, DateTimeOffset hour)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO SyncState (NetworkId, LastSyncedHour, LastAttemptUtc) VALUES ($id, $h, $t) " +
                "ON CONFLICT(NetworkId) DO UPDATE SET " +
                "  LastSyncedHour = excluded.LastSyncedHour, LastAttemptUtc = excluded.LastAttemptUtc;";
            cmd.Parameters.AddWithValue("$id", networkId);
            cmd.Parameters.AddWithValue("$h", hour.UtcDateTime.ToString(HourFormat));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Networks that have ever recorded a byte. These are the only ones worth querying on a
    /// routine refresh — skipping the rest is what turns a 93-second sweep into a 2.5-second one.
    /// </summary>
    public HashSet<string> GetProductiveKeys()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT n.ProfileName FROM Network n " +
                "WHERE EXISTS (SELECT 1 FROM HourlyUsage h WHERE h.NetworkId = n.Id);";

            var keys = new HashSet<string>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read()) keys.Add(r.GetString(0));
            return keys;
        }
    }

    /// <summary>One row per stored network, for diagnostics. Ordered so duplicates sit together.</summary>
    public IReadOnlyList<(long Id, string ProfileName, string AdapterId, int Source, long Hours, long Bytes)> DescribeNetworks()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT n.Id, n.ProfileName, n.AdapterId, " +
                "       COALESCE(MIN(h.Source), -1), COUNT(h.HourUtc), " +
                "       COALESCE(SUM(h.BytesSent + h.BytesReceived), 0) " +
                "FROM Network n LEFT JOIN HourlyUsage h ON h.NetworkId = n.Id " +
                "GROUP BY n.Id ORDER BY n.ProfileName, n.Id;";

            var rows = new List<(long, string, string, int, long, long)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(5)));
            return rows;
        }
    }

    public DateTimeOffset? GetEarliestRecordedHour()
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MIN(HourUtc) FROM HourlyUsage;";
            return cmd.ExecuteScalar() is string s ? ParseHour(s) : null;
        }
    }

    /// <summary>Per-network totals across a window.</summary>
    public IReadOnlyList<NetworkTotal> GetTotals(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT n.Id, n.ProfileName, n.Kind, n.ColorIndex, n.IsMetered, " +
                "       COALESCE(SUM(h.BytesSent), 0), COALESCE(SUM(h.BytesReceived), 0) " +
                "FROM Network n " +
                "LEFT JOIN HourlyUsage h ON h.NetworkId = n.Id AND h.HourUtc >= $from AND h.HourUtc < $to " +
                "GROUP BY n.Id " +
                "ORDER BY (COALESCE(SUM(h.BytesSent), 0) + COALESCE(SUM(h.BytesReceived), 0)) DESC;";
            cmd.Parameters.AddWithValue("$from", fromUtc.UtcDateTime.ToString(HourFormat));
            cmd.Parameters.AddWithValue("$to", toUtc.UtcDateTime.ToString(HourFormat));

            var totals = new List<NetworkTotal>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                totals.Add(new NetworkTotal(
                    r.GetInt64(0),
                    r.GetString(1),
                    (NetworkKind)r.GetInt32(2),
                    r.GetInt32(3),
                    r.GetInt32(4) != 0,
                    r.GetInt64(5),
                    r.GetInt64(6)));
            }
            return totals;
        }
    }

    /// <summary>
    /// Hourly series, zero-filled so charts have no gaps. Pass one or more network ids to see
    /// just those; pass null or an empty set for every network combined.
    /// </summary>
    public IReadOnlyList<UsageBucket> GetCombinedSeries(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyCollection<long>? networkIds = null)
    {
        lock (_sync)
        {
            // Ids come from our own table, never from user input, but they are still bound as
            // parameters rather than pasted into the SQL.
            var filter = "";
            if (networkIds is { Count: > 0 })
                filter = "AND NetworkId IN (" + string.Join(",", networkIds.Select((_, i) => "$n" + i)) + ") ";

            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT HourUtc, SUM(BytesSent), SUM(BytesReceived) FROM HourlyUsage " +
                "WHERE HourUtc >= $from AND HourUtc < $to " + filter +
                "GROUP BY HourUtc;";
            cmd.Parameters.AddWithValue("$from", fromUtc.UtcDateTime.ToString(HourFormat));
            cmd.Parameters.AddWithValue("$to", toUtc.UtcDateTime.ToString(HourFormat));

            if (networkIds is { Count: > 0 })
            {
                var i = 0;
                foreach (var id in networkIds) cmd.Parameters.AddWithValue("$n" + i++, id);
            }

            var found = new Dictionary<DateTimeOffset, UsageBucket>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var hour = ParseHour(r.GetString(0));
                    found[hour] = new UsageBucket(hour, r.GetInt64(1), r.GetInt64(2));
                }
            }

            var series = new List<UsageBucket>();
            for (var h = UsageProvider.FloorToHour(fromUtc); h < toUtc; h = h.AddHours(1))
                series.Add(found.TryGetValue(h, out var b) ? b : new UsageBucket(h, 0, 0));

            return series;
        }
    }

    public UsageSummary GetSummary(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        // lock is reentrant, so the two calls below stay consistent with each other.
        lock (_sync)
        {
            var networks = GetTotals(fromUtc, toUtc);
            var series = GetCombinedSeries(fromUtc, toUtc);
            return new UsageSummary(
                fromUtc,
                toUtc,
                networks.Sum(n => n.BytesSent),
                networks.Sum(n => n.BytesReceived),
                networks,
                series);
        }
    }

    private static DateTimeOffset ParseHour(string s) =>
        DateTimeOffset.ParseExact(
            s, HourFormat, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private void Execute(string sql)
    {
        lock (_sync)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_sync) _db.Dispose();
    }
}
