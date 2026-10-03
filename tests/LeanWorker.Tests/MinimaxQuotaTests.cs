using Xunit;

namespace LeanWorker.Tests;

public class MinimaxQuotaTests
{
    [Fact]
    public void Remaining_percent_becomes_used_and_windows_are_named_by_length()
    {
        QuotaReading q = Quota.ParseMinimax("minimax-coding-plan", /*lang=json,strict*/ """
            {"model_remains":[
              {"start_time":1790694000000,"end_time":1790712000000,"current_interval_total_count":0,"current_interval_usage_count":0,
               "model_name":"general","current_weekly_total_count":0,"current_weekly_usage_count":0,"weekly_end_time":1791158400000,
               "current_interval_remaining_percent":99,"current_weekly_remaining_percent":97},
              {"start_time":1790640000000,"end_time":1790726400000,"current_interval_total_count":3,"current_interval_usage_count":3,
               "model_name":"video","current_weekly_total_count":21,"current_weekly_usage_count":21,"weekly_end_time":1791158400000,
               "current_interval_remaining_percent":100,"current_weekly_remaining_percent":100}],
             "base_resp":{"status_code":0,"status_msg":"success"}}
            """);
        Assert.Equal(["5h", "weekly", "video-24h", "video-weekly"], q.Windows.Select(w => w.Name), StringComparer.Ordinal);
        Assert.Equal([1m, 3m, 0m, 0m], q.Windows.Select(w => w.Percent));
        Assert.Equal("3 of 3 requests left", q.Windows[2].Detail);
        Assert.Null(q.Windows[0].Detail);
    }

    [Fact]
    public void The_text_models_block_is_5h_even_when_cut_at_the_day_boundary()
    {
        QuotaReading q = Quota.ParseMinimax("m", /*lang=json,strict*/ """
            {"model_remains":[{"model_name":"general","start_time":1790712000000,"end_time":1790726400000,
              "current_interval_remaining_percent":100,"current_weekly_remaining_percent":100}],"base_resp":{"status_code":0}}
            """);
        Assert.Equal("5h", q.Windows[0].Name);
    }

    [Fact]
    public void An_error_status_is_a_launch_failure() => _ = Assert.Throws<LaunchException>(() => Quota.ParseMinimax("m", /*lang=json,strict*/ """{"base_resp":{"status_code":1004,"status_msg":"invalid key"}}"""));
}
