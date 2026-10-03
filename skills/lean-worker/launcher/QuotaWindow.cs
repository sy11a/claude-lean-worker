namespace LeanWorker;

internal sealed record QuotaWindow(string Name, decimal Percent, DateTimeOffset? ResetsAt, string? Detail);