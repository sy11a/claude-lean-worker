namespace LeanWorker;

internal sealed record ModelPrice(string Key, Price Base, long? AboveTokens, Price? Above, decimal UsdRate)
{
    public decimal Cost(Usage u)
    {
        Price p = AboveTokens is { } t && Above is not null && u.Context > t ? Above : Base;
        decimal perMTok = (u.Input * p.Input) + ((u.Output + u.Reasoning) * p.Output) + (u.CacheRead * p.CacheRead)
                      + (u.CacheWrite5m * p.CacheWrite) + (u.CacheWrite1h * p.CacheWrite1h);
        return perMTok / 1_000_000m * UsdRate;
    }
}