using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class MeterTests
{
    private const string BookJson = """
        { "rates": {"USD": 1}, "models": { "anthropic/m": { "input": 1000000, "output": 0 } } }
        """;

    private static PriceBook Book() => PriceBook.FromJson(JsonNode.Parse(BookJson)!.AsObject());

    [Fact]
    public void Wraps_up_at_the_share_and_reports_the_cap_once()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        Meter meter = new(Book(), "anthropic", dir, budget: 10, wrapUpAt: 0.5m, "HANDOFF please");
        Assert.False(meter.Add(new Usage("1", "m", 4, 0, 0, 0, 0, 0)));
        Assert.False(meter.WrappedUp);
        Assert.False(meter.Add(new Usage("2", "m", 2, 0, 0, 0, 0, 0)));
        Assert.True(meter.WrappedUp);
        Assert.Contains("HANDOFF please", File.ReadAllText(meter.WrapUpFile), StringComparison.Ordinal);
        Assert.True(meter.Add(new Usage("3", "m", 5, 0, 0, 0, 0, 0)));
        Assert.False(meter.Add(new Usage("4", "m", 1, 0, 0, 0, 0, 0)));
        Assert.Equal(12m, meter.Spent);
    }

    [Fact]
    public void A_call_reported_twice_counts_once()
    {
        Meter meter = new(Book(), "anthropic", Directory.CreateTempSubdirectory().FullName, 100, wrapUpAt: null, string.Empty);
        _ = meter.Add(new Usage("1", "m", 1, 0, 0, 0, 0, 0));
        _ = meter.Add(new Usage("1", "m", 3, 0, 0, 0, 0, 0));
        Assert.Equal(3m, meter.Spent);
        _ = Assert.Single(meter.Calls());
    }
}
