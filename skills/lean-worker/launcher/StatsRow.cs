namespace LeanWorker;

/// <summary>
/// One line of <c>stats</c>: the runs of one profile on one model.
/// </summary>
internal sealed record StatsRow(string Profile, string Model, int Runs, int Success, int WrappedUp, int Escalations,
                                decimal CostUsd, decimal? CostPerSuccessUsd, decimal? QuotaPctPerRun);