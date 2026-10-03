using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class ModelTraitsTests
{
    private const string BookJson = """
        { "providers": { "p": { "anthropicBaseUrl": "https://x" }, "q": {} },
          "modelTraits": { "_comment": "x", "MiniMax-M": { "note": "family" },
                           "MiniMax-M3": { "allowedTools": ["Bash(head:*)"], "note": "m3" } } }
        """;

    private static PriceBook Book() => PriceBook.FromJson(JsonNode.Parse(BookJson)!.AsObject());

    [Fact]
    public void Traits_match_the_model_id_without_its_provider_longest_key_first()
    {
        ModelTraits t = Book().Traits("minimax-m3-0930")!;
        Assert.Equal(("MiniMax-M3", "m3"), (t.Key, t.Note));
        Assert.Equal(["Bash(head:*)"], t.AllowedTools);
        Assert.Equal("MiniMax-M", Book().Traits("MiniMax-M2.7")!.Key);
        Assert.Null(Book().Traits("glm-5.3"));
        Assert.Null(Book().Traits("_comment"));
    }

    [Fact]
    public void Runtime_follows_the_provider_when_none_is_given()
    {
        Assert.Equal("claude", Launcher.DefaultRuntime(Book().Provider("anthropic")));
        Assert.Equal("claude", Launcher.DefaultRuntime(Book().Provider("p")));
        Assert.Equal("opencode", Launcher.DefaultRuntime(Book().Provider("q")));
    }
}
