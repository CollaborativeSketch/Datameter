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
}
