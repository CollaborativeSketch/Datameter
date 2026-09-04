namespace Datameter.Core;

public enum NetworkKind
{
    WiFi = 0,
    Ethernet = 1,
    Cellular = 2,
    Other = 3
}

/// <summary>
/// A network with recorded usage. Identity is ProfileName; AdapterId is informational and is
/// only known while the network is available.
/// </summary>
public sealed record NetworkRecord(
    long Id,
    string ProfileName,
    string? AdapterId,
    NetworkKind Kind,
    bool IsMetered,
    int ColorIndex,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>One hour of usage. HourUtc is the start of the hour, always UTC.</summary>
public readonly record struct UsageBucket(DateTimeOffset HourUtc, long BytesSent, long BytesReceived)
{
    public long Total => BytesSent + BytesReceived;
}

/// <summary>Aggregated usage for one network over a requested window.</summary>
public sealed record NetworkTotal(
    long NetworkId,
    string ProfileName,
    NetworkKind Kind,
    int ColorIndex,
    bool IsMetered,
    long BytesSent,
    long BytesReceived)
{
    public long Total => BytesSent + BytesReceived;
}

/// <summary>Everything one screen needs: the headline figure plus its breakdown.</summary>
public sealed record UsageSummary(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long BytesSent,
    long BytesReceived,
    IReadOnlyList<NetworkTotal> Networks,
    IReadOnlyList<UsageBucket> Series)
{
    public long Total => BytesSent + BytesReceived;

    /// <summary>Networks that actually moved bytes, largest first.</summary>
    public IReadOnlyList<NetworkTotal> ActiveNetworks =>
        Networks.Where(n => n.Total > 0).OrderByDescending(n => n.Total).ToList();
}

public static class ByteFormat
{
    private const double KB = 1024d, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;

    /// <summary>Formats bytes the way Windows does: a value plus a unit, never more than 2 decimals.</summary>
    public static string Humanize(long bytes)
    {
        double b = bytes;
        return b switch
        {
            >= TB => $"{b / TB:0.##} TB",
            >= GB => $"{b / GB:0.##} GB",
            >= MB => $"{b / MB:0.#} MB",
            >= KB => $"{b / KB:0} KB",
            _ => $"{bytes} B"
        };
    }

    /// <summary>
    /// Formats a value in the unit some other figure would choose.
    ///
    /// A set of related figures should read in one unit. Left to itself a ruler running to 2 GB
    /// labels its quarters "1 GB" and then "512 MB", which is correct and reads as a mistake.
    /// </summary>
    public static string HumanizeIn(long bytes, long anchor)
    {
        double a = anchor;
        var (size, unit) = a switch
        {
            >= TB => (TB, "TB"),
            >= GB => (GB, "GB"),
            >= MB => (MB, "MB"),
            >= KB => (KB, "KB"),
            _ => (1d, "B")
        };

        return $"{bytes / size:0.##} {unit}";
    }

    /// <summary>
    /// Formats a rate for the live meter. Unlike <see cref="Humanize"/> this stays in KB/s down
    /// to zero rather than dropping to bytes: the meter updates every second beside a fixed
    /// layout, and a unit that changes under idle traffic makes the reading jump about.
    /// </summary>
    public static string HumanizeRate(long bytesPerSecond)
    {
        double b = bytesPerSecond;
        return b switch
        {
            >= GB => $"{b / GB:0.##} GB/s",
            >= MB => $"{b / MB:0.0} MB/s",
            _ => $"{b / KB:0.0} KB/s"
        };
    }
}
