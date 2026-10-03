using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class TemplateTests
{
    private static string Templates()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "templates")))
        {
            d = d.Parent;
        }

        return Path.Combine(d!.FullName, "skills", "lean-worker", "templates");
    }

    [Fact]
    public void Chain_template_ends_every_chain_in_claude_and_prices_every_model()
    {
        JsonNode doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Templates(), "profiles-chains.json")))!;
        JsonNode book = JsonNode.Parse(File.ReadAllText(Path.Combine(Templates(), "..", "launcher", "prices.json")))!;
        foreach ((string? name, JsonNode? p) in doc["profiles"]!.AsObject())
        {
            if (p!["model"] is not JsonArray arr)
            {
                continue;
            }

            List<string> chain = [.. arr.Select(m => m!.GetValue<string>())];
            Assert.True(chain.Count >= 2 && chain[^1].StartsWith("claude-", StringComparison.Ordinal), $"{name}: {string.Join(',', chain)}");
            foreach (string? id in chain.Where(m => m.Contains('/', StringComparison.Ordinal)))
            {
                (string? prov, string? model) = (id[..id.IndexOf('/', StringComparison.Ordinal)], id[(id.IndexOf('/', StringComparison.Ordinal) + 1)..]);
                string priced = book["providers"]?[prov]?["priceAs"]?.GetValue<string>() ?? prov;
                Assert.True(book["models"]?[$"{priced}/{model}"] is not null, $"{name}: {id} has no price-book entry");
            }
        }
    }
}
