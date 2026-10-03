// Live spend meter. The launcher feeds it every usage record the worker streams; it prices each API call with
// the price book, writes <run>/wrapup.json once spend passes the wrap-up threshold (the runtime's pre-tool
// hook then denies every tool call), and reports when the hard cap is reached so the launcher can stop the worker.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class Meter(PriceBook prices, string provider, string runDir, decimal budget, decimal? wrapUpAt, string handoffText)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (Usage Usage, decimal Cost)> _calls = [];
    private readonly List<string> _order = [];

    public decimal Spent { get; private set; }
    public bool WrappedUp { get; private set; }
    public bool CapReached { get; private set; }
    public List<string> Notes { get; } = [];
    public string WrapUpFile => Path.Combine(runDir, "wrapup.json");

    /// <summary>Adds or replaces one API call's usage (a call can be reported more than once; the last report wins).
    /// Returns true when the hard cap has just been reached.</summary>
    public bool Add(Usage u)
    {
        lock (_lock)
        {
            ModelPrice price;
            string? note;
            try { price = prices.Resolve(provider, u.Model, out note); }
            catch (LaunchException ex)
            {
                // The model was checked before launch; one the stream reports on top of it must still count.
                ModelPrice? dearest = prices.Dearest();
                if (dearest is null)
                {
                    throw;
                }

                price = dearest;
                note = $"{ex.Message}; priced as the dearest model to keep the budget safe";
            }
            if (note is not null && !Notes.Contains(note))
            {
                Notes.Add(note);
            }

            decimal cost = price.Cost(u);
            if (_calls.TryGetValue(u.Id, out (Usage Usage, decimal Cost) prev))
            {
                Spent -= prev.Cost;
            }
            else
            {
                _order.Add(u.Id);
            }

            _calls[u.Id] = (u, cost);
            Spent += cost;

            if (wrapUpAt is { } share && !WrappedUp && Spent >= budget * share)
            {
                WrappedUp = true;
                string reason = string.Create(CultureInfo.InvariantCulture, $"Budget nearly spent (about ${Spent:0.####} of ${budget:0.####}). Tool calls are now blocked. ") + handoffText;
                File.WriteAllText(WrapUpFile, new JsonObject
                {
                    ["at"] = DateTimeOffset.Now.ToString("o"),
                    ["spent_usd"] = Spent,
                    ["threshold_usd"] = budget * share,
                    ["budget_usd"] = budget,
                    ["reason"] = reason,
                }.ToJsonString(Json.Indented), Json.Utf8);
            }
            if (!CapReached && Spent >= budget)
            {
                CapReached = true;
                return true;
            }
            return false;
        }
    }

    public List<Usage> Calls()
    {
        lock (_lock)
        {
            return [.. _order.Select(id => _calls[id].Usage)];
        }
    }

    public const string HandoffInstruction =
        "Stop working. Make your next message the final report, and put this block first:\n" +
        "HANDOFF\n" +
        "- Done: what is finished and verified\n" +
        "- Remaining: what is left, in order\n" +
        "- Files touched: paths, and whether each edit is complete or half-done\n" +
        "- State: does it build, do the tests pass, as far as you know\n" +
        "- Next step: the first thing a fresh worker should do";
}
