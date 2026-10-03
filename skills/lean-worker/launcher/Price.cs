namespace LeanWorker;

internal sealed record Price(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, decimal CacheWrite1h);