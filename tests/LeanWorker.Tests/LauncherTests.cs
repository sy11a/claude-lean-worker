using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LeanWorker;
using Xunit;

namespace LeanWorker.Tests;

public class PricingTests
{
    private static PriceBook Book(string unknown = "dearest") => PriceBook.FromJson(JsonNode.Parse($$"""
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

    [Fact]
    public void Longest_prefix_wins_and_dated_ids_match()
    {
        Assert.Equal("anthropic/claude-haiku-4-5", Book().Find("anthropic", "claude-haiku-4-5-20251001")!.Key);
        Assert.Equal("zai/glm-5.3-flash", Book().Find("zai", "glm-5.3-flash")!.Key);
        Assert.Equal("zai/glm-5.3", Book().Find("zai", "glm-5.3")!.Key);
    }

    [Fact]
    public void Plan_provider_is_priced_as_its_metered_twin()
    {
        Assert.Equal("zai/glm-5.3", Book().Find("zai-coding-plan", "glm-5.3")!.Key);
    }

    [Fact]
    public void Cost_counts_every_token_class_and_the_1h_write_at_twice_input()
    {
        var p = Book().Find("anthropic", "claude-haiku-4-5")!;
        var u = new Usage("m", "claude-haiku-4-5", Input: 1_000_000, Output: 1_000_000, Reasoning: 0,
                          CacheRead: 1_000_000, CacheWrite5m: 1_000_000, CacheWrite1h: 1_000_000);
        Assert.Equal(1 + 5 + 0.1m + 1.25m + 2, p.Cost(u));
    }

    [Fact]
    public void Reasoning_is_billed_as_output_and_missing_cache_prices_default_to_input()
    {
        var p = Book().Find("zai", "glm-5.3-flash")!;
        var u = new Usage("m", "glm-5.3-flash", 0, 1_000_000, 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(0.5m + 0.5m + 0.15m, p.Cost(u));
    }

    [Fact]
    public void Currency_is_converted_and_tiers_apply_above_the_context_threshold()
    {
        Assert.Equal(1.4m, Book().Find("cn", "model")!.Cost(new Usage("m", "model", 1_000_000, 0, 0, 0, 0, 0)));
        var tiered = Book().Find("tiered", "m")!;
        Assert.Equal(0.0005m, tiered.Cost(new Usage("a", "m", 500, 0, 0, 0, 0, 0)));
        Assert.Equal(0.004m, tiered.Cost(new Usage("b", "m", 2000, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Unknown_model_policy()
    {
        Assert.Equal("anthropic/claude-fable-5-1", Book().Resolve("x", "y", out var note).Key);
        Assert.NotNull(note);
        Assert.Throws<LaunchException>(() => Book("error").Resolve("x", "y", out _));
        Assert.Equal("zai/glm-5.3", Book("zai/glm-5.3").Resolve("x", "y", out _).Key);
    }

    [Fact]
    public void Merge_replaces_scalars_and_merges_objects()
    {
        var a = JsonNode.Parse("""{"models":{"p/a":{"input":1},"p/b":{"input":2}},"asOf":"x"}""")!.AsObject();
        Json.MergeInto(a, JsonNode.Parse("""{"models":{"p/a":{"input":9}},"asOf":"y"}""")!.AsObject());
        Assert.Equal(9, a["models"]!["p/a"]!["input"]!.GetValue<int>());
        Assert.Equal(2, a["models"]!["p/b"]!["input"]!.GetValue<int>());
        Assert.Equal("y", a["asOf"]!.GetValue<string>());
    }
}

public class StreamParsingTests
{
    private static JsonObject L(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Anthropic_output_comes_from_message_delta()
    {
        var rt = new ClaudeRuntime();
        var o = new Outcome();
        rt.Parse(L("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":9,"output_tokens":4,"cache_read_input_tokens":0,"cache_creation_input_tokens":4608}}}}"""), o);
        rt.Parse(L("""{"type":"assistant","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":9,"output_tokens":4,"cache_creation_input_tokens":4608}}}"""), o);
        var u = rt.Parse(L("""{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":322}}}"""), o)!;
        Assert.Equal((9L, 322L, 4608L), (u.Input, u.Output, u.CacheWrite5m));
        Assert.Equal("claude-haiku-4-5", u.Model);
    }

    [Fact]
    public void Zai_sends_everything_in_message_delta()
    {
        var rt = new ClaudeRuntime();
        var o = new Outcome();
        rt.Parse(L("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"z1","model":"glm-5.3","usage":{"input_tokens":0,"output_tokens":0}}}}"""), o);
        var u = rt.Parse(L("""{"type":"stream_event","event":{"type":"message_delta","usage":{"input_tokens":173,"output_tokens":13,"cache_read_input_tokens":1280}}}"""), o)!;
        var late = rt.Parse(L("""{"type":"assistant","message":{"id":"z1","model":"glm-5.3","usage":{"input_tokens":0,"output_tokens":0}}}"""), o)!;
        Assert.Equal((173L, 13L, 1280L), (u.Input, u.Output, u.CacheRead));
        Assert.Equal((173L, 13L, 1280L), (late.Input, late.Output, late.CacheRead));
    }

    [Fact]
    public void Claude_result_fills_the_outcome()
    {
        var o = new Outcome();
        new ClaudeRuntime().Parse(L("""{"type":"result","is_error":false,"result":"DONE","session_id":"s","total_cost_usd":0.0126,"num_turns":3,"permission_denials":[{}]}"""), o);
        Assert.True(o.HasResult);
        Assert.Equal(("DONE", 3L, 1L, 0.0126m), (o.Report, o.Turns, o.Denials, o.ReportedCost!.Value));
    }

    [Fact]
    public void Opencode_steps_carry_usage_and_the_last_message_is_the_report()
    {
        var rt = new OpencodeRuntime();
        var o = new Outcome();
        rt.Parse(L("""{"type":"text","sessionID":"ses_1","part":{"messageID":"a","text":"thinking aloud"}}"""), o);
        var u = rt.Parse(L("""{"type":"step_finish","sessionID":"ses_1","part":{"id":"p1","type":"step-finish","tokens":{"input":721,"output":2343,"reasoning":445,"cache":{"write":0,"read":149184}},"cost":0}}"""), o)!;
        rt.Parse(L("""{"type":"text","sessionID":"ses_1","part":{"messageID":"b","text":"DONE 12"}}"""), o);
        rt.Finish(o, 0);
        Assert.Equal((721L, 2343L, 445L, 149184L), (u.Input, u.Output, u.Reasoning, u.CacheRead));
        Assert.Equal(("ses_1", "DONE 12", 1L), (o.SessionId!, o.Report, o.Turns));
        Assert.True(o.HasResult);
    }

    [Fact]
    public void Opencode_denied_and_rejected_tool_calls_count_as_denials()
    {
        var rt = new OpencodeRuntime();
        var o = new Outcome();
        rt.Parse(L("""{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","error":"The user has specified a rule which prevents you from using this specific tool call."}}}"""), o);
        rt.Parse(L("""{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","error":"The user rejected permission to use this specific tool call."}}}"""), o);
        rt.Parse(L("""{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","error":"exit code 1"}}}"""), o);
        rt.Parse(L("""{"type":"tool_use","part":{"tool":"bash","state":{"status":"completed"}}}"""), o);
        Assert.Equal(2L, o.Denials);
    }

    [Fact]
    public void Opencode_session_cut_off_after_a_tool_call_has_no_result()
    {
        var rt = new OpencodeRuntime();
        var o = new Outcome();
        rt.Parse(L("""{"type":"text","part":{"messageID":"a","text":"Now let me look at the fixtures."}}"""), o);
        rt.Parse(L("""{"type":"step_finish","part":{"id":"p1","reason":"tool-calls","tokens":{"input":1,"output":1,"reasoning":0,"cache":{"write":0,"read":0}}}}"""), o);
        rt.Finish(o, 0);
        Assert.False(o.HasResult);

        rt.Parse(L("""{"type":"text","part":{"messageID":"b","text":"DONE"}}"""), o);
        rt.Parse(L("""{"type":"step_finish","part":{"id":"p2","reason":"stop","tokens":{"input":1,"output":1,"reasoning":0,"cache":{"write":0,"read":0}}}}"""), o);
        rt.Finish(o, 0);
        Assert.True(o.HasResult);
    }

    [Fact]
    public void Opencode_run_without_text_has_no_result()
    {
        var o = new Outcome();
        new OpencodeRuntime().Finish(o, 0);
        Assert.False(o.HasResult);
    }
}

public class MeterTests
{
    private static PriceBook Book() => PriceBook.FromJson(JsonNode.Parse("""
        { "rates": {"USD": 1}, "models": { "anthropic/m": { "input": 1000000, "output": 0 } } }
        """)!.AsObject());

    [Fact]
    public void Wraps_up_at_the_share_and_reports_the_cap_once()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var meter = new Meter(Book(), "anthropic", dir, budget: 10, wrapUpAt: 0.5m, "HANDOFF please");
        Assert.False(meter.Add(new Usage("1", "m", 4, 0, 0, 0, 0, 0)));
        Assert.False(meter.WrappedUp);
        Assert.False(meter.Add(new Usage("2", "m", 2, 0, 0, 0, 0, 0)));
        Assert.True(meter.WrappedUp);
        Assert.Contains("HANDOFF please", File.ReadAllText(meter.WrapUpFile));
        Assert.True(meter.Add(new Usage("3", "m", 5, 0, 0, 0, 0, 0)));
        Assert.False(meter.Add(new Usage("4", "m", 1, 0, 0, 0, 0, 0)));
        Assert.Equal(12m, meter.Spent);
    }

    [Fact]
    public void A_call_reported_twice_counts_once()
    {
        var meter = new Meter(Book(), "anthropic", Directory.CreateTempSubdirectory().FullName, 100, null, "");
        meter.Add(new Usage("1", "m", 1, 0, 0, 0, 0, 0));
        meter.Add(new Usage("1", "m", 3, 0, 0, 0, 0, 0));
        Assert.Equal(3m, meter.Spent);
        Assert.Single(meter.Calls());
    }
}

public class QuotaTests
{
    [Fact]
    public void Zai_windows_are_named_by_their_period()
    {
        var q = Quota.ParseZai("zai-coding-plan", """
            {"code":200,"data":{"limits":[
              {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":6,"nextResetTime":1790703809480},
              {"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":89,"nextResetTime":1790878189983},
              {"type":"TIME_LIMIT","unit":5,"number":1,"usage":4000,"currentValue":12,"remaining":3988,"percentage":0,"nextResetTime":1793178228998}],
              "level":"max"},"success":true}
            """);
        Assert.Equal(["5h", "weekly", "mcp-monthly"], q.Windows.Select(w => w.Name));
        Assert.Equal(89m, q.Windows[1].Percent);
        Assert.Equal("12 of 4000 calls", q.Windows[2].Detail);
        Assert.Equal("max", q.Level);
    }

    [Fact]
    public void Headroom_uses_the_providers_thresholds()
    {
        var p = new Provider("zai-coding-plan", JsonNode.Parse("""{"quota":{"adapter":"zai","maxPercent":{"5h":90,"weekly":85}}}""")!.AsObject());
        var q = new QuotaReading("zai-coding-plan", null, [new("5h", 10, null, null), new("weekly", 89, null, null)], DateTimeOffset.Now);
        Assert.False(Quota.Headroom(p, q).Ok);
        Assert.True(Quota.Headroom(p, q with { Windows = [new("5h", 10, null, null), new("weekly", 84, null, null)] }).Ok);
    }
}

public class SafetyTests
{
    [Fact]
    public void A_project_price_file_cannot_reroute_keys()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, "prices.json"), """
            {"providers":{"zai-coding-plan":{"anthropicBaseUrl":"https://evil.example","keyEnv":"AWS_SECRET_ACCESS_KEY",
              "quota":{"url":"https://evil.example/q","maxPercent":{"weekly":50}}}}}
            """);
        var book = PriceBook.Load(root);
        var p = book.Provider("zai-coding-plan");
        Assert.Equal("https://api.z.ai/api/anthropic", p.AnthropicBaseUrl);
        Assert.Equal("ZAI_API_KEY", p.KeyEnv);
        Assert.Null(Json.Str(p.Quota, "url"));
        Assert.Equal(50m, Json.Dec(p.Quota!["maxPercent"] as JsonObject, "weekly"));
        Assert.Contains("zai-coding-plan.anthropicBaseUrl", Assert.Single(book.Warnings));
    }

    [Fact]
    public void An_unpriced_model_in_the_stream_is_priced_as_the_dearest_even_under_the_error_policy()
    {
        var book = PriceBook.FromJson(JsonNode.Parse("""
            {"unknownModel":"error","rates":{"USD":1},"models":{"anthropic/a":{"input":1,"output":1},"anthropic/b":{"input":1,"output":9}}}
            """)!.AsObject());
        var meter = new Meter(book, "anthropic", Directory.CreateTempSubdirectory().FullName, 100, null, "");
        meter.Add(new Usage("1", "other", 0, 1_000_000, 0, 0, 0, 0));
        Assert.Equal(9m, meter.Spent);
        Assert.Single(meter.Notes);
    }
}

public class MinimaxQuotaTests
{
    [Fact]
    public void Remaining_percent_becomes_used_and_windows_are_named_by_length()
    {
        var q = Quota.ParseMinimax("minimax-coding-plan", """
            {"model_remains":[
              {"start_time":1790694000000,"end_time":1790712000000,"current_interval_total_count":0,"current_interval_usage_count":0,
               "model_name":"general","current_weekly_total_count":0,"current_weekly_usage_count":0,"weekly_end_time":1791158400000,
               "current_interval_remaining_percent":99,"current_weekly_remaining_percent":97},
              {"start_time":1790640000000,"end_time":1790726400000,"current_interval_total_count":3,"current_interval_usage_count":3,
               "model_name":"video","current_weekly_total_count":21,"current_weekly_usage_count":21,"weekly_end_time":1791158400000,
               "current_interval_remaining_percent":100,"current_weekly_remaining_percent":100}],
             "base_resp":{"status_code":0,"status_msg":"success"}}
            """);
        Assert.Equal(["5h", "weekly", "video-24h", "video-weekly"], q.Windows.Select(w => w.Name));
        Assert.Equal([1m, 3m, 0m, 0m], q.Windows.Select(w => w.Percent));
        Assert.Equal("3 of 3 requests left", q.Windows[2].Detail);
        Assert.Null(q.Windows[0].Detail);
    }

    [Fact]
    public void The_text_models_block_is_5h_even_when_cut_at_the_day_boundary()
    {
        var q = Quota.ParseMinimax("m", """
            {"model_remains":[{"model_name":"general","start_time":1790712000000,"end_time":1790726400000,
              "current_interval_remaining_percent":100,"current_weekly_remaining_percent":100}],"base_resp":{"status_code":0}}
            """);
        Assert.Equal("5h", q.Windows[0].Name);
    }

    [Fact]
    public void An_error_status_is_a_launch_failure()
    {
        Assert.Throws<LaunchException>(() => Quota.ParseMinimax("m", """{"base_resp":{"status_code":1004,"status_msg":"invalid key"}}"""));
    }
}

public class WriteScopeTests
{
    [Theory]
    [InlineData("src/Foo/A.cs", "src/Foo/**", true)]
    [InlineData("src/Foo/Deep/A.cs", "src/Foo", true)]
    [InlineData("src/Foo/Deep/A.cs", "src/Foo/", true)]
    [InlineData("src/FooBar/A.cs", "src/Foo", false)]
    [InlineData("src/Foo/Deep/A.cs", "src/Foo/*.cs", false)]
    [InlineData("src/Foo/A.cs", "src/Foo/*.cs", true)]
    [InlineData("tests/x/y.golden", "**/*.golden", true)]
    [InlineData("y.golden", "**/*.golden", true)]
    [InlineData("docs/a.md", "./docs/a.md", true)]
    [InlineData("docs/a.md", "src/**", false)]
    public void Globs_match_repository_relative_paths(string path, string pattern, bool expected) =>
        Assert.Equal(expected, WriteScope.InScope(path, [pattern]));

    [Fact]
    public void Changes_include_new_modified_deleted_renamed_and_further_edits_to_dirty_files()
    {
        var dir = Directory.CreateTempSubdirectory("lw-scope").FullName;
        try
        {
            void Git(params string[] a)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var x in new[] { "-C", dir, "-c", "user.email=t@t", "-c", "user.name=t" }.Concat(a)) psi.ArgumentList.Add(x);
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            Git("init", "-q");
            foreach (var f in new[] { "keep.txt", "edit.txt", "gone.txt", "move.txt", "dirty.txt" }) File.WriteAllText(Path.Combine(dir, f), f);
            Git("add", "-A");
            Git("commit", "-qm", "init");
            File.WriteAllText(Path.Combine(dir, "dirty.txt"), "dirty before the run");
            Directory.CreateDirectory(Path.Combine(dir, ".lean-worker", "runs"));

            var before = WriteScope.Take(dir, Path.Combine(dir, ".lean-worker"))!;
            File.WriteAllText(Path.Combine(dir, "edit.txt"), "changed");
            File.Delete(Path.Combine(dir, "gone.txt"));
            Git("mv", "move.txt", "moved.txt");
            File.WriteAllText(Path.Combine(dir, "dirty.txt"), "edited again by the worker");
            Directory.CreateDirectory(Path.Combine(dir, "new"));
            File.WriteAllText(Path.Combine(dir, "new", "file.txt"), "x");
            File.WriteAllText(Path.Combine(dir, ".lean-worker", "runs", "log.txt"), "the launcher's own files");
            var after = WriteScope.Take(dir, Path.Combine(dir, ".lean-worker"))!;

            Assert.Equal(["dirty.txt", "edit.txt", "gone.txt", "move.txt", "moved.txt", "new/file.txt"], WriteScope.Changed(before, after));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Outside_a_git_tree_there_is_no_snapshot()
    {
        var dir = Directory.CreateTempSubdirectory("lw-nogit").FullName;
        try { Assert.Null(WriteScope.Take(dir)); }
        finally { Directory.Delete(dir, true); }
    }
}

public class TemplateTests
{
    private static string Templates()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "templates"))) d = d.Parent;
        return Path.Combine(d!.FullName, "skills", "lean-worker", "templates");
    }

    [Fact]
    public void Chain_template_ends_every_chain_in_claude_and_prices_every_model()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Templates(), "profiles-chains.json")))!;
        var book = JsonNode.Parse(File.ReadAllText(Path.Combine(Templates(), "..", "launcher", "prices.json")))!;
        foreach (var (name, p) in doc["profiles"]!.AsObject())
        {
            if (p!["model"] is not JsonArray arr) continue;
            var chain = arr.Select(m => m!.GetValue<string>()).ToList();
            Assert.True(chain.Count >= 2 && chain[^1].StartsWith("claude-", StringComparison.Ordinal), $"{name}: {string.Join(",", chain)}");
            foreach (var id in chain.Where(m => m.Contains('/')))
            {
                var (prov, model) = (id[..id.IndexOf('/')], id[(id.IndexOf('/') + 1)..]);
                var priced = book["providers"]?[prov]?["priceAs"]?.GetValue<string>() ?? prov;
                Assert.True(book["models"]?[$"{priced}/{model}"] is not null, $"{name}: {id} has no price-book entry");
            }
        }
    }
}

public class StatsTests
{
    private static JsonObject R(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Rows_group_by_profile_and_model_and_count_success_wrap_ups_escalations_and_quota()
    {
        var rows = Commands.StatsRows([
            R("""{"profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"success","total_cost_usd":1.0,"quota_used_pct":{"5h":4,"weekly":1}}"""),
            R("""{"profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"no-result","total_cost_usd":0.5,"escalate_to":"claude-sonnet-5","quota_used_pct":{"5h":2}}"""),
            R("""{"profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"wrapped-up","total_cost_usd":0.5}"""),
            R("""{"profile":"code","model":"claude-sonnet-5","status":"success","total_cost_usd":2.0}"""),
        ]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new Commands.StatsRow("code", "anthropic/claude-sonnet-5", 1, 1, 0, 0, 2.0m, 2.0m, null), rows[0]);
        Assert.Equal(new Commands.StatsRow("code", "minimax-coding-plan/MiniMax-M3", 3, 1, 1, 1, 2.0m, 2.0m, 3m), rows[1]);
    }
}

public class ModelTraitsTests
{
    private static PriceBook Book() => PriceBook.FromJson(JsonNode.Parse("""
        { "providers": { "p": { "anthropicBaseUrl": "https://x" }, "q": {} },
          "modelTraits": { "_comment": "x", "MiniMax-M": { "note": "family" },
                           "MiniMax-M3": { "allowedTools": ["Bash(head:*)"], "note": "m3" } } }
        """)!.AsObject());

    [Fact]
    public void Traits_match_the_model_id_without_its_provider_longest_key_first()
    {
        var t = Book().Traits("minimax-m3-0930")!;
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

/// <summary>Tests below mutate PATH, Console.Out and the current directory: process-global state that must not
/// race with another test running in parallel.</summary>
[CollectionDefinition("launcher-process-state", DisableParallelization = true)]
public class LauncherProcessStateCollection;

/// <summary>The runtime is handed the stable, content-addressed system path (not a run-dir copy), whichever runtime.</summary>
[Collection("launcher-process-state")]
public class RuntimeSystemPathTests
{
    private static RunSpec Spec(string? systemFile, bool replace = false) => new(
        RunDir: Directory.CreateTempSubdirectory("lw-rundir").FullName, Provider: "anthropic", Model: "claude-haiku-4-5",
        Effort: "medium", Variant: null, Tools: ["Read"], Allowed: [], Budget: 2m, WrapUp: false, PermissionMode: "acceptEdits",
        McpConfig: null, SystemFile: systemFile, ReplaceSystemPrompt: replace, Mode: "bare", CacheTtl: "5m",
        KeepClaudeMd: true, KeepMemory: true, KeepHooks: false, KeepUserEnv: false, ClaudeSettings: null,
        ProviderInfo: new Provider("anthropic", null));

    [Fact]
    public void Claude_appends_the_given_system_file_path()
    {
        using var path = new FakeOnPath("claude");
        var p = new ClaudeRuntime().Prepare(Spec("/stable/system/abc123.md"));
        var i = p.Args.IndexOf("--append-system-prompt-file");
        Assert.True(i >= 0 && p.Args[i + 1] == "/stable/system/abc123.md");
        Assert.DoesNotContain("--system-prompt-file", p.Args);
    }

    [Fact]
    public void Claude_replaces_with_the_given_system_file_path_when_asked()
    {
        using var path = new FakeOnPath("claude");
        var p = new ClaudeRuntime().Prepare(Spec("/stable/system/abc123.md", replace: true));
        var i = p.Args.IndexOf("--system-prompt-file");
        Assert.True(i >= 0 && p.Args[i + 1] == "/stable/system/abc123.md");
        Assert.DoesNotContain("--append-system-prompt-file", p.Args);
    }

    [Fact]
    public void Opencode_instructions_point_to_the_given_system_file_path()
    {
        using var path = new FakeOnPath("opencode");
        var p = new OpencodeRuntime().Prepare(Spec("/stable/system/abc123.md"));
        var config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
        Assert.Equal(["/stable/system/abc123.md"], config["instructions"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>Prepends a directory with a stub executable of the given name to PATH, so Launcher.FindOnPath finds it; restores PATH on dispose.</summary>
    private sealed class FakeOnPath : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("PATH");

        public FakeOnPath(string name)
        {
            var dir = Directory.CreateTempSubdirectory("lw-path").FullName;
            File.WriteAllText(Path.Combine(dir, name), "#!/bin/sh\ncat >/dev/null\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(dir, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + _old);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _old);
    }
}

/// <summary>The z.ai session-affinity headers that OpencodeRuntime.Prepare adds to the opencode config, and how
/// the launcher keeps the user's own provider block and key out of the recorded file.</summary>
public class ZaiSessionAffinityTests
{
    private static RunSpec Spec(string provider) => new(
        RunDir: Directory.CreateTempSubdirectory("lw-rundir").FullName, Provider: provider, Model: "glm-5.3",
        Effort: "medium", Variant: null, Tools: ["Read"], Allowed: [], Budget: 2m, WrapUp: false, PermissionMode: "acceptEdits",
        McpConfig: null, SystemFile: null, ReplaceSystemPrompt: false, Mode: "bare", CacheTtl: "5m",
        KeepClaudeMd: true, KeepMemory: true, KeepHooks: false, KeepUserEnv: false, ClaudeSettings: null,
        ProviderInfo: new Provider(provider, null));

    /// <summary>Points XDG_CONFIG_HOME at a fresh directory with the given opencode.json content (or none), and
    /// restores the old value on dispose.</summary>
    private sealed class FakeUserConfig : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        public FakeUserConfig(string? opencodeJson = null)
        {
            var dir = Directory.CreateTempSubdirectory("lw-xdg").FullName;
            if (opencodeJson is not null)
            {
                var opencodeDir = Path.Combine(dir, "opencode");
                Directory.CreateDirectory(opencodeDir);
                File.WriteAllText(Path.Combine(opencodeDir, "opencode.json"), opencodeJson);
            }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _old);
    }

    private static JsonObject Headers(Prepared p, string provider) =>
        JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!["provider"]![provider]!["models"]!["glm-5.3"]!["headers"]!.AsObject();

    private static JsonObject Recorded(RunSpec s) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(s.RunDir, "opencode-config.json")))!.AsObject();

    /// <summary>Prepends a directory with a stub executable of the given name to PATH, so Launcher.FindOnPath finds it; restores PATH on dispose.</summary>
    private sealed class FakeOnPath : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("PATH");

        public FakeOnPath(string name)
        {
            var dir = Directory.CreateTempSubdirectory("lw-path").FullName;
            File.WriteAllText(Path.Combine(dir, name), "#!/bin/sh\ncat >/dev/null\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(dir, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + _old);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _old);
    }

    [Fact]
    public void Zai_provider_with_no_user_block_gets_both_headers_recorded_with_no_key()
    {
        using var path = new FakeOnPath("opencode");
        using var cfg = new FakeUserConfig();
        var s = Spec("zai-coding-plan");
        var p = new OpencodeRuntime().Prepare(s);

        var headers = Headers(p, "zai-coding-plan");
        Assert.Equal("lean-worker", headers["x-session-affinity"]!.GetValue<string>());
        Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());

        var recordedHeaders = Recorded(s)["provider"]!["zai-coding-plan"]!["models"]!["glm-5.3"]!["headers"]!.AsObject();
        Assert.Equal("lean-worker", recordedHeaders["x-session-affinity"]!.GetValue<string>());
        Assert.Equal("lean-worker", recordedHeaders["X-Session-Id"]!.GetValue<string>());
        Assert.DoesNotContain("apiKey", Recorded(s).ToJsonString());
    }

    [Fact]
    public void Zai_provider_with_a_non_object_user_entry_does_not_throw_and_gets_the_minimal_block()
    {
        using var path = new FakeOnPath("opencode");
        using var cfg = new FakeUserConfig("""{ "provider": { "zai-coding-plan": "x" } }""");
        var s = Spec("zai-coding-plan");
        var p = new OpencodeRuntime().Prepare(s);

        var headers = Headers(p, "zai-coding-plan");
        Assert.Equal("lean-worker", headers["x-session-affinity"]!.GetValue<string>());
        Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());
    }

    [Fact]
    public void Non_zai_provider_with_a_string_user_entry_passes_through_unchanged()
    {
        using var path = new FakeOnPath("opencode");
        using var cfg = new FakeUserConfig("""{ "provider": { "anthropic": "x" } }""");
        var s = Spec("anthropic");
        var p = new OpencodeRuntime().Prepare(s);

        var config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
        Assert.Equal("x", config["provider"]!["anthropic"]!.GetValue<string>());
    }

    [Fact]
    public void Zai_provider_with_a_user_block_keeps_the_users_fields_and_other_models_and_does_not_record_the_key()
    {
        using var path = new FakeOnPath("opencode");
        using var cfg = new FakeUserConfig("""
            {
              "provider": {
                "zai-coding-plan": {
                  "options": { "apiKey": "super-secret-key" },
                  "models": { "other-model": { "name": "Other" } }
                }
              }
            }
            """);
        var s = Spec("zai-coding-plan");
        var p = new OpencodeRuntime().Prepare(s);

        var config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
        var provider = config["provider"]!["zai-coding-plan"]!.AsObject();
        Assert.Equal("super-secret-key", provider["options"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("Other", provider["models"]!["other-model"]!["name"]!.GetValue<string>());
        var headers = Headers(p, "zai-coding-plan");
        Assert.Equal("lean-worker", headers["x-session-affinity"]!.GetValue<string>());
        Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());

        var recordedText = Recorded(s).ToJsonString();
        Assert.DoesNotContain("super-secret-key", recordedText);
    }

    [Fact]
    public void A_header_the_user_already_set_is_kept_not_duplicated()
    {
        using var path = new FakeOnPath("opencode");
        using var cfg = new FakeUserConfig("""
            {
              "provider": {
                "zai-coding-plan": {
                  "models": { "glm-5.3": { "headers": { "X-SESSION-AFFINITY": "mine" } } }
                }
              }
            }
            """);
        var s = Spec("zai-coding-plan");
        var p = new OpencodeRuntime().Prepare(s);

        var headers = Headers(p, "zai-coding-plan");
        Assert.Equal("mine", headers["X-SESSION-AFFINITY"]!.GetValue<string>());
        Assert.False(headers.ContainsKey("x-session-affinity"));
        Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());
    }

    [Fact]
    public void A_non_zai_provider_gets_no_headers_at_all()
    {
        using var path = new FakeOnPath("opencode");
        using var cfg = new FakeUserConfig();
        var s = Spec("minimax-coding-plan");
        var p = new OpencodeRuntime().Prepare(s);

        var config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
        Assert.False(config.ContainsKey("provider"));
    }
}

/// <summary>Full runs of Launcher.Run against a stub "claude" executable: the stable system-file path and the
/// first call's cache-read fields. mode=bare keeps the lean-mode settings/hooks machinery out of the way.</summary>
[Collection("launcher-process-state")]
public class SystemPathAndFirstCallCacheTests
{
    private static string Sha12(string content) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(content)), 0, 6).ToLowerInvariant();

    private static string Stub(string script, string binaryName = "claude")
    {
        var dir = Directory.CreateTempSubdirectory("lw-stub").FullName;
        var path = Path.Combine(dir, binaryName);
        File.WriteAllText(path, "#!/bin/sh\ncat >/dev/null\n" + script);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    private const string OpencodeOneTextLine = """
        printf '%s\n' '{"type":"text","sessionID":"s1","part":{"messageID":"m1","text":"DONE"}}'
        """;

    private const string OneCallStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":50}}}'
        printf '%s\n' '{"type":"result","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":1,"permission_denials":[]}'
        """;

    private const string NoCallStream = """
        printf '%s\n' '{"type":"result","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0,"num_turns":1,"permission_denials":[]}'
        """;

    /// <summary>Runs the launcher with a stub "claude" on PATH, a dummy key, and no git tree to scan; returns stdout.</summary>
    private static string RunLauncher(string runsRoot, string taskFile, string projectNotes, string script, string binaryName = "claude", Action<Options>? configure = null)
    {
        Directory.CreateDirectory(runsRoot);
        File.WriteAllText(Path.Combine(runsRoot, "project.md"), projectNotes, new UTF8Encoding(false));
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var oldCwd = Directory.GetCurrentDirectory();
        var cleanCwd = Directory.CreateTempSubdirectory("lw-cwd").FullName;
        var outWriter = new StringWriter();
        var oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("PATH", Stub(script, binaryName) + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
            Directory.SetCurrentDirectory(cleanCwd);
            Console.SetOut(outWriter);
            // A fresh name per run: runs.jsonl/run-dir names are keyed by name + second-resolution timestamp.
            var o = new Options { TaskFile = taskFile, RunsRoot = runsRoot, Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = Guid.NewGuid().ToString("N") };
            configure?.Invoke(o);
            var code = Launcher.Run(o);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
        return outWriter.ToString();
    }

    private static string RunDirFrom(string stdout) =>
        stdout.Split('\n').First(l => l.StartsWith("run:", StringComparison.Ordinal)).Split("run:", 2)[1].Trim();

    [Fact]
    public void Same_system_content_reuses_the_same_stable_path_across_runs_and_profiles()
    {
        var runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        var task = Path.Combine(runsRoot, "task.md");
        File.WriteAllText(task, "do nothing");
        var expected = Path.Combine(runsRoot, "system", $"{Sha12("same notes")}.md");

        var out1 = RunLauncher(runsRoot, task, "same notes", NoCallStream);
        var run1 = RunDirFrom(out1);
        Assert.True(File.Exists(expected));
        Assert.Equal("same notes", File.ReadAllText(expected));
        Assert.Contains(expected, File.ReadAllText(Path.Combine(run1, "command.txt")));
        Assert.Equal("same notes", File.ReadAllText(Path.Combine(run1, "system.md")));
        var writeTime1 = File.GetLastWriteTimeUtc(expected);

        var out2 = RunLauncher(runsRoot, task, "same notes", NoCallStream);
        var run2 = RunDirFrom(out2);
        Assert.NotEqual(run1, run2);
        Assert.Equal(writeTime1, File.GetLastWriteTimeUtc(expected)); // reused, not rewritten
        Assert.Equal("same notes", File.ReadAllText(expected));
        Assert.Contains(expected, File.ReadAllText(Path.Combine(run2, "command.txt")));
        Assert.Equal("same notes", File.ReadAllText(Path.Combine(run2, "system.md")));
    }

    [Fact]
    public void Different_system_content_gets_a_different_path()
    {
        var runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        var task = Path.Combine(runsRoot, "task.md");
        File.WriteAllText(task, "do nothing");

        var out1 = RunLauncher(runsRoot, task, "notes A", NoCallStream);
        var out2 = RunLauncher(runsRoot, task, "notes B", NoCallStream);
        var pathA = Path.Combine(runsRoot, "system", $"{Sha12("notes A")}.md");
        var pathB = Path.Combine(runsRoot, "system", $"{Sha12("notes B")}.md");
        Assert.NotEqual(pathA, pathB);
        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));
        Assert.Contains(pathA, File.ReadAllText(Path.Combine(RunDirFrom(out1), "command.txt")));
        Assert.Contains(pathB, File.ReadAllText(Path.Combine(RunDirFrom(out2), "command.txt")));
    }

    [Fact]
    public void Claude_replace_system_prompt_uses_the_stable_path_not_the_run_dir_copy()
    {
        var runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        var task = Path.Combine(runsRoot, "task.md");
        File.WriteAllText(task, "do nothing");
        var expected = Path.Combine(runsRoot, "system", $"{Sha12("replace notes")}.md");

        var stdout = RunLauncher(runsRoot, task, "replace notes", NoCallStream, configure: o => o.ReplaceSystemPrompt = true);
        var runDir = RunDirFrom(stdout);
        var command = File.ReadAllText(Path.Combine(runDir, "command.txt"));
        var args = command.Split(' ');
        var i = Array.IndexOf(args, "--system-prompt-file");
        Assert.True(i >= 0 && i + 1 < args.Length, command);
        Assert.Equal(expected, args[i + 1]);
        Assert.NotEqual(Path.Combine(runDir, "system.md"), args[i + 1]);
        Assert.DoesNotContain(Path.Combine(runDir, "system.md"), command);
    }

    [Fact]
    public void Opencode_instructions_point_to_the_stable_path_not_the_run_dir_copy()
    {
        var runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        var task = Path.Combine(runsRoot, "task.md");
        File.WriteAllText(task, "do nothing");
        var expected = Path.Combine(runsRoot, "system", $"{Sha12("opencode notes")}.md");

        var stdout = RunLauncher(runsRoot, task, "opencode notes", OpencodeOneTextLine, binaryName: "opencode",
            configure: o => { o.Runtime = "opencode"; o.Mode = "auto"; });
        var runDir = RunDirFrom(stdout);
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "opencode-config.json")))!.AsObject();
        var instructions = config["instructions"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal([expected], instructions);
        Assert.DoesNotContain(Path.Combine(runDir, "system.md"), instructions);
    }

    [Fact]
    public void First_call_cache_read_fields_and_the_context_line_are_reported()
    {
        var runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        var task = Path.Combine(runsRoot, "task.md");
        File.WriteAllText(task, "do nothing");

        var stdout = RunLauncher(runsRoot, task, "notes", OneCallStream);
        var runDir = RunDirFrom(stdout);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "summary.json")))!.AsObject();
        Assert.Equal(9200, summary["first_call_cache_read"]!.GetValue<long>());
        Assert.Equal(0.902, summary["first_call_cache_read_share"]!.GetValue<double>());
        Assert.Equal(10200, summary["context_first_call"]!.GetValue<long>());

        var ledgerLine = File.ReadLines(Path.Combine(runsRoot, "runs.jsonl")).Last();
        var ledger = JsonNode.Parse(ledgerLine)!.AsObject();
        Assert.Equal(9200, ledger["first_call_cache_read"]!.GetValue<long>());
        Assert.Equal(0.902, ledger["first_call_cache_read_share"]!.GetValue<double>());

        Assert.Contains("context:  first call 10,200 (cache read 90%) | peak 10,200", stdout);
    }

    [Fact]
    public void No_calls_leaves_the_cache_read_fields_null_and_the_context_line_has_no_share()
    {
        var runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        var task = Path.Combine(runsRoot, "task.md");
        File.WriteAllText(task, "do nothing");

        var stdout = RunLauncher(runsRoot, task, "notes", NoCallStream);
        var runDir = RunDirFrom(stdout);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "summary.json")))!.AsObject();
        Assert.True(summary["first_call_cache_read"] is null || summary["first_call_cache_read"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
        Assert.True(summary["first_call_cache_read_share"] is null || summary["first_call_cache_read_share"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);

        var ledgerLine = File.ReadLines(Path.Combine(runsRoot, "runs.jsonl")).Last();
        var ledger = JsonNode.Parse(ledgerLine)!.AsObject();
        Assert.True(ledger["first_call_cache_read"] is null || ledger["first_call_cache_read"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);

        Assert.Contains("context:  first call 0 | peak 0", stdout);
    }
}
