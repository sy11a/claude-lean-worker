namespace LeanWorker;

/// <summary>
/// What the stream said besides usage: the report, the outcome and bookkeeping.
/// </summary>
internal sealed class Outcome
{
    public bool HasResult;
    public bool IsError;
    public string Report = string.Empty;
    public string? SessionId;
    public string? Subtype;
    public string? TerminalReason;
    public decimal? ReportedCost;
    public long Turns;
    public long Denials;
    public long Thinking;
    public string? LastMessageId;
    public string? LastStepReason;
    public readonly List<string> Texts = [];
}