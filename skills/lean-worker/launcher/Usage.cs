namespace LeanWorker;

/// <summary>
/// One API call's token usage. Output excludes reasoning; both are billed at the output price.
/// </summary>
internal sealed record Usage(string Id, string Model, long Input, long Output, long Reasoning,
                             long CacheRead, long CacheWrite5m, long CacheWrite1h)
{
    public long Context => Input + CacheRead + CacheWrite5m + CacheWrite1h;
}