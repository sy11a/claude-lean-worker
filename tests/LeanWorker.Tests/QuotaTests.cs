using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class QuotaTests
{
    [Fact]
    public void Zai_windows_are_named_by_their_period()
    {
        QuotaReading q = Quota.ParseZai("zai-coding-plan", /*lang=json,strict*/ """
            {"code":200,"data":{"limits":[
              {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":6,"nextResetTime":1790703809480},
              {"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":89,"nextResetTime":1790878189983},
              {"type":"TIME_LIMIT","unit":5,"number":1,"usage":4000,"currentValue":12,"remaining":3988,"percentage":0,"nextResetTime":1793178228998}],
              "level":"max"},"success":true}
            """);
        Assert.Equal(["5h", "weekly", "mcp-monthly"], q.Windows.Select(w => w.Name), StringComparer.Ordinal);
        Assert.Equal(89m, q.Windows[1].Percent);
        Assert.Equal("12 of 4000 calls", q.Windows[2].Detail);
        Assert.Equal("max", q.Level);
    }

    [Fact]
    public void Headroom_uses_the_providers_thresholds()
    {
        Provider p = new("zai-coding-plan", JsonNode.Parse("""{"quota":{"adapter":"zai","maxPercent":{"5h":90,"weekly":85}}}""")!.AsObject());
        QuotaReading q = new("zai-coding-plan", Level: null, [new("5h", 10, ResetsAt: null, Detail: null), new("weekly", 89, ResetsAt: null, Detail: null)], DateTimeOffset.Now);
        Assert.False(Quota.Headroom(p, q).Ok);
        Assert.True(Quota.Headroom(p, q with { Windows = [new("5h", 10, ResetsAt: null, Detail: null), new("weekly", 84, ResetsAt: null, Detail: null)] }).Ok);
    }
}
