using System.Text.Json.Nodes;

namespace LeanWorker;

internal interface IRuntime
{
    public string Name { get; }
    public Prepared Prepare(RunSpec s);

    /// <summary>
    /// Reads one stream line; returns the API call's usage if the line carries one.
    /// </summary>
    public Usage? Parse(JsonObject line, Outcome o);

    /// <summary>
    /// Settles the outcome after the process exits.
    /// </summary>
    public void Finish(Outcome o, int exitCode);

    /// <summary>
    /// Whether a stream line is kept in stream.jsonl (token-by-token deltas are not).
    /// </summary>
    public bool Record(string line) => true;
}