using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class PricingTests
{
    private static PriceBook Book(string unknown = "dearest")
    {
        return PriceBook.FromJson(JsonNode.Parse($$"""
        {
          "unknownModel": "{{unknown}}",
          "rates": { "USD": 1, "CNY": 0.14 },
          "providers": { "zai-coding-plan": { "billing": "subscription", "priceAs": "zai" } },
          "models": {
            "anthropic/claude-haiku-4-5": { "input": 1, "output": 5, "cacheRead": 0.1, "cacheWrite": 1.25 },
            "anthropic/claude-fable-5-1": { "input": 10, "output": 50 },
            "zai/glm-5.3": { "input": 1.4, "output": 4.4, "cacheRead": 0.26, "cacheWrite": 0 },
            "zai/glm-5.3-flash": { "input": 0.15, "output": 0.5 },
            "cn/model": { "input": 10, "output": 10, "currency": "CNY" },
            "tiered/m": { "input": 1, "output": 1, "above": { "tokens": 1000, "input": 2 } }
          }
        }
        """)!.AsObject());
    }

    [Fact]
    public void Longest_prefix_wins_and_dated_ids_match()
    {
        Assert.Equal("anthropic/claude-haiku-4-5", Book().Find("anthropic", "claude-haiku-4-5-20251001")!.Key);
        Assert.Equal("zai/glm-5.3-flash", Book().Find("zai", "glm-5.3-flash")!.Key);
        Assert.Equal("zai/glm-5.3", Book().Find("zai", "glm-5.3")!.Key);
    }

    [Fact]
    public void Plan_provider_is_priced_as_its_metered_twin() => Assert.Equal("zai/glm-5.3", Book().Find("zai-coding-plan", "glm-5.3")!.Key);

    [Fact]
    public void Cost_counts_every_token_class_and_the_1h_write_at_twice_input()
    {
        ModelPrice p = Book().Find("anthropic", "claude-haiku-4-5")!;
        Usage u = new("m", "claude-haiku-4-5", Input: 1_000_000, Output: 1_000_000, Reasoning: 0,
                          CacheRead: 1_000_000, CacheWrite5m: 1_000_000, CacheWrite1h: 1_000_000);
        Assert.Equal(1 + 5 + 0.1m + 1.25m + 2, p.Cost(u));
    }

    [Fact]
    public void Reasoning_is_billed_as_output_and_missing_cache_prices_default_to_input()
    {
        ModelPrice p = Book().Find("zai", "glm-5.3-flash")!;
        Usage u = new("m", "glm-5.3-flash", 0, 1_000_000, 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(0.5m + 0.5m + 0.15m, p.Cost(u));
    }

    [Fact]
    public void Currency_is_converted_and_tiers_apply_above_the_context_threshold()
    {
        Assert.Equal(1.4m, Book().Find("cn", "model")!.Cost(new Usage("m", "model", 1_000_000, 0, 0, 0, 0, 0)));
        ModelPrice tiered = Book().Find("tiered", "m")!;
        Assert.Equal(0.0005m, tiered.Cost(new Usage("a", "m", 500, 0, 0, 0, 0, 0)));
        Assert.Equal(0.004m, tiered.Cost(new Usage("b", "m", 2000, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Unknown_model_policy()
    {
        Assert.Equal("anthropic/claude-fable-5-1", Book().Resolve("x", "y", out string? note).Key);
        Assert.NotNull(note);
        _ = Assert.Throws<LaunchException>(() => Book("error").Resolve("x", "y", out _));
        Assert.Equal("zai/glm-5.3", Book("zai/glm-5.3").Resolve("x", "y", out _).Key);
    }

    [Fact]
    public void Merge_replaces_scalars_and_merges_objects()
    {
        JsonObject a = JsonNode.Parse("""{"models":{"p/a":{"input":1},"p/b":{"input":2}},"asOf":"x"}""")!.AsObject();
        Json.MergeInto(a, JsonNode.Parse("""{"models":{"p/a":{"input":9}},"asOf":"y"}""")!.AsObject());
        Assert.Equal(9, a["models"]!["p/a"]!["input"]!.GetValue<int>());
        Assert.Equal(2, a["models"]!["p/b"]!["input"]!.GetValue<int>());
        Assert.Equal("y", a["asOf"]!.GetValue<string>());
    }
}
