using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>Golden tests of Launcher.RunAsync against a stub "claude" executable (mode bare, Anthropic model, dummy key).
/// They pin the printed result block, summary.json, the runs.jsonl row and the run dir's files, so a refactor of
/// RunAsync cannot change behaviour unnoticed. Only volatile parts (paths, durations, timestamps) are normalized.</summary>
internal static class RunAsyncGolden
{
    public const string Notes = "golden notes";

    public static readonly string[] SummaryKeys =
    [
        "schema_version", "timestamp", "name", "run_dir", "profile", "runtime", "provider", "model", "model_reason", "effort",
        "mode", "billing", "cache_ttl", "hooks", "status", "subtype", "terminal_reason", "exit_code", "num_turns", "api_calls",
        "duration_ms", "total_cost_usd", "reported_cost_usd", "budget_usd", "wrap_up_usd", "wrapped_up", "hook_checks",
        "continued_from", "escalate_to", "model_traits", "tokens", "context_first_call", "first_call_cache_read",
        "first_call_cache_read_share", "context_peak", "permission_denials", "write_scope", "changed_files", "out_of_scope",
        "session_id", "quota_before", "quota_after", "quota_used_pct", "notes",
    ];

    public const string BareWrapUpNote = "bare mode skips hooks, so wrap-up is off; the budget is still enforced";

    private static string Sha12(string content) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content)), 0, 6).ToLowerInvariant();

    public static string SystemPath(string root) => Path.Combine(root, "system", $"{Sha12(Notes)}.md");

    private static string Stub(string script)
    {
        string dir = Directory.CreateTempSubdirectory("lw-stub").FullName;
        string path = Path.Combine(dir, "claude");
        File.WriteAllText(path, "#!/bin/sh\ncat >/dev/null\n" + script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    public static string NewRoot()
    {
        string root = Directory.CreateTempSubdirectory("lw-runs").FullName;
        File.WriteAllText(Path.Combine(root, "project.md"), Notes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(root, "task.md"), "do nothing");
        return root;
    }

    /// <summary>Runs RunAsync with the stub on PATH; returns the exit code and stdout, or rethrows what RunAsync throws.</summary>
    public static async Task<(int Code, string Stdout)> Run(string root, string script, Action<Options>? configure = null, string? apiKey = "dummy-test-key")
    {
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string oldCwd = Directory.GetCurrentDirectory();
        string cleanCwd = Directory.CreateTempSubdirectory("lw-cwd").FullName;
        StringWriter outWriter = new();
        TextWriter oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("PATH", Stub(script) + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
            Directory.SetCurrentDirectory(cleanCwd);
            Console.SetOut(outWriter);
            Options o = new() { TaskFile = Path.Combine(root, "task.md"), RunsRoot = root, Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "golden" };
            configure?.Invoke(o);
            int code = await Launcher.RunAsync(o);
            return (code, outWriter.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    public static string RunDirFrom(string stdout) =>
        stdout.Split('\n').First(l => l.StartsWith("run:", StringComparison.Ordinal)).Split("run:", 2)[1].Trim();

    /// <summary>Paths become &lt;ROOT&gt; / &lt;RUN&gt;, the "0m00s" duration &lt;DUR&gt;, a run dir's timestamp &lt;TS&gt;.</summary>
    public static string Normalize(string text, string root)
    {
        string t = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        t = Regex.Replace(t, Regex.Escape(Path.Combine(root, "runs")) + @"/\d{8}-\d{6}-([A-Za-z0-9._-]+)", "<RUN:$1>");
        t = t.Replace(root, "<ROOT>", StringComparison.Ordinal);
        return Regex.Replace(t, @"\b\d+m\d{2}s\b", "<DUR>");
    }

    public static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    public static JsonObject Summary(string runDir) => JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "summary.json")))!.AsObject();

    public static void AssertSummaryKeys(JsonObject summary) =>
        Assert.Equal(SummaryKeys, summary.Select(kv => kv.Key).ToArray());

    public static void AssertFiles(string runDir, params string[] expected) =>
        Assert.Equal(expected, Directory.GetFiles(runDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());

    public static void AssertLedgerHasOneRowEqualTo(string root, JsonObject summary)
    {
        string[] rows = File.ReadAllLines(Path.Combine(root, "runs.jsonl"));
        string row = Assert.Single(rows);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(row), summary), row);
    }

    public static string[] Notes_(JsonObject s) => [.. s["notes"]!.AsArray().Select(n => n!.GetValue<string>())];

    public static string BareCommand(string root, string maxBudget = "2") =>
        "claude -p --bare --model claude-haiku-4-5 --effort medium --append-system-prompt-file " + SystemPath(root) +
        " --tools Read,Edit,Write,Glob,Grep,Bash --strict-mcp-config --permission-mode acceptEdits --max-budget-usd " + maxBudget +
        " --no-session-persistence --output-format stream-json --verbose --include-partial-messages";
}

[Collection("launcher-process-state")]
public class RunAsyncCharacterizationTests
{
    private const string SuccessStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":50}}}'
        printf '%s\n' '{"type":"assistant","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":3,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}'
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":2,"permission_denials":[{"tool_name":"Bash"}],"usage":{"output_tokens_details":{"thinking_tokens":7}}}'
        """;

    private const string ErrorStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":100,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":10}}}'
        printf '%s\n' '{"type":"result","subtype":"error_during_execution","is_error":true,"result":"it broke","session_id":"s2","total_cost_usd":0.0002,"num_turns":1,"permission_denials":[]}'
        """;

    private const string CrashScript = """
        i=1
        while [ $i -le 60 ]; do echo "err line $i" >&2; i=$((i+1)); done
        exit 3
        """;

    private const string BudgetScript = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        sleep 30
        """;

    private const string LongReportStream = """
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"ABCDEFGHIJKLMNOPQRST","session_id":"s3","total_cost_usd":0,"num_turns":1,"permission_denials":[]}'
        """;

    [Fact]
    public async Task Success_prints_the_golden_block_and_writes_summary_ledger_and_files()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.Run(root, SuccessStream);

        Assert.Equal(0, code);
        Assert.Equal(RunAsyncGolden.Lines(
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   success  (subtype=success, reason=completed, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     2 turns, 1 API calls, <DUR>",
            "cost:     $0.0022 (list price; metered; runtime reported $0.0100)",
            "budget:   $2, wrap-up off",
            "tokens:   input 1,000 | cache write 0 | cache read 9,200 | output 50 (thinking 7)",
            "context:  first call 10,200 (cache read 90%) | peak 10,200",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "WARNING:  1 permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.",
            "--- worker report ---",
            "DONE"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);

        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(RunAsyncGolden.BareCommand(root), RunAsyncGolden.Normalize(File.ReadAllText(Path.Combine(runDir, "command.txt")), root).Replace("<ROOT>", root, StringComparison.Ordinal));
        Assert.Equal("DONE", File.ReadAllText(Path.Combine(runDir, "report.md")));
        Assert.Equal("do nothing", File.ReadAllText(Path.Combine(runDir, "task.md")));
        Assert.Equal(RunAsyncGolden.Notes, File.ReadAllText(Path.Combine(runDir, "system.md")));
    }

    [Fact]
    public async Task Error_result_exits_1_with_status_error()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.Run(root, ErrorStream);

        Assert.Equal(1, code);
        Assert.Equal(RunAsyncGolden.Lines(
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   error  (subtype=error_during_execution, reason=, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     1 turns, 1 API calls, <DUR>",
            "cost:     $0.0002 (list price; metered)",
            "budget:   $2, wrap-up off",
            "tokens:   input 100 | cache write 0 | cache read 0 | output 10 (thinking 0)",
            "context:  first call 100 (cache read 0%) | peak 100",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "it broke"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("error", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal("it broke", File.ReadAllText(Path.Combine(runDir, "report.md")));
    }

    [Fact]
    public async Task No_result_and_a_non_zero_exit_is_crashed_and_echoes_exactly_40_stderr_lines()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.Run(root, CrashScript);

        Assert.Equal(1, code);
        List<string> expected =
        [
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   crashed  (subtype=, reason=, exit=3)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     0 turns, 0 API calls, <DUR>",
            "cost:     $0.0000 (list price; metered)",
            "budget:   $2, wrap-up off",
            "tokens:   input 0 | cache write 0 | cache read 0 | output 0 (thinking 0)",
            "context:  first call 0 | peak 0",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "(no report text)",
            "--- stderr (first 40 lines) ---",
        ];
        expected.AddRange(Enumerable.Range(1, 40).Select(i => $"err line {i}"));
        Assert.Equal(RunAsyncGolden.Lines([.. expected]), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("crashed", s["status"]!.GetValue<string>());
        Assert.Equal(3, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(60, File.ReadAllLines(Path.Combine(runDir, "stderr.txt")).Length);
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(runDir, "report.md")));
    }

    [Fact]
    public async Task Exceeding_the_budget_stops_the_worker_with_status_budget_exceeded()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.Run(root, BudgetScript, o => o.MaxBudgetUsd = 0.01m);

        Assert.Equal(1, code);
        Assert.Equal(RunAsyncGolden.Lines(
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   budget-exceeded  (subtype=, reason=, exit=137)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     0 turns, 1 API calls, <DUR>",
            "cost:     $1.0000 (list price; metered)",
            "budget:   $0.01, wrap-up off, STOPPED at the budget",
            "tokens:   input 1,000,000 | cache write 0 | cache read 0 | output 0 (thinking 0)",
            "context:  first call 1,000,000 (cache read 0%) | peak 1,000,000",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "(no report text)"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("budget-exceeded", s["status"]!.GetValue<string>());
        Assert.Equal(137, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(RunAsyncGolden.BareCommand(root, "0.01"), File.ReadAllText(Path.Combine(runDir, "command.txt")));
    }

    [Fact]
    public async Task A_long_report_is_truncated_to_report_max_chars_with_a_pointer_to_report_md()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.Run(root, LongReportStream, o => o.ReportMaxChars = 10);

        Assert.Equal(0, code);
        Assert.Equal(RunAsyncGolden.Lines(
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   success  (subtype=success, reason=, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     1 turns, 0 API calls, <DUR>",
            "cost:     $0.0000 (list price; metered)",
            "budget:   $2, wrap-up off",
            "tokens:   input 0 | cache write 0 | cache read 0 | output 0 (thinking 0)",
            "context:  first call 0 | peak 0",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "ABCDEFGHIJ",
            "[truncated; full report: <RUN:golden>/report.md]"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal("ABCDEFGHIJKLMNOPQRST", File.ReadAllText(Path.Combine(runDir, "report.md")));
    }

    [Fact]
    public async Task Continue_from_writes_the_previous_task_and_report_into_task_md_and_inherits_the_name()
    {
        string root = RunAsyncGolden.NewRoot();
        (_, string firstOut) = await RunAsyncGolden.Run(root, ErrorStream);
        string firstDir = RunAsyncGolden.RunDirFrom(firstOut);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken); // run dirs are keyed by second-resolution timestamp

        (int code, string stdout) = await RunAsyncGolden.Run(root, SuccessStream, o => { o.TaskFile = null; o.Name = null; o.ContinueFrom = firstDir; });

        Assert.Equal(0, code);
        Assert.Equal(RunAsyncGolden.Lines(
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   success  (subtype=success, reason=completed, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     2 turns, 1 API calls, <DUR>",
            "cost:     $0.0022 (list price; metered; runtime reported $0.0100)",
            "budget:   $2, wrap-up off",
            "tokens:   input 1,000 | cache write 0 | cache read 9,200 | output 50 (thinking 7)",
            "context:  first call 10,200 (cache read 90%) | peak 10,200",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "WARNING:  1 permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.",
            "--- worker report ---",
            "DONE"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        Assert.NotEqual(firstDir, runDir, StringComparer.Ordinal);
        string nl = Environment.NewLine;
        Assert.Equal(
            "do nothing" + nl + nl + "## Continuation (lean-worker)" + nl + nl +
            "A previous worker on this task stopped before finishing (status: error). Its report is below. " +
            "Check the current state first (for example `git status` and `git diff --stat`) and do not redo finished work." +
            nl + nl + "it broke" + nl,
            File.ReadAllText(Path.Combine(runDir, "task.md")));

        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("golden", s["name"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(firstDir), s["continued_from"]!.GetValue<string>());
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        string[] rows = File.ReadAllLines(Path.Combine(root, "runs.jsonl"));
        Assert.Equal(2, rows.Length);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(rows[1]), s), rows[1]);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
    }

    [Fact]
    public async Task Missing_task_and_continue_from_is_a_launch_error()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(() => RunAsyncGolden.Run(root, "exit 0", o => o.TaskFile = null));
        Assert.Equal("--task <file> or --continue-from <run-dir> is required", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Invalid_effort_is_a_launch_error()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(() => RunAsyncGolden.Run(root, "exit 0", o => o.Effort = "ultra"));
        Assert.Equal("invalid effort 'ultra'", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Invalid_cache_ttl_is_a_launch_error()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(() => RunAsyncGolden.Run(root, "exit 0", o => o.CacheTtl = "2h"));
        Assert.Equal("invalid cache TTL '2h' (5m | 1h | default)", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Wrap_up_at_one_is_a_launch_error()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(() => RunAsyncGolden.Run(root, "exit 0", o => o.WrapUpAt = 1m));
        Assert.Equal("invalid wrap-up share 1 (0 = off, else below 1)", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Mode_bare_without_a_key_is_a_launch_error()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(() => RunAsyncGolden.Run(root, "exit 0", apiKey: null));
        Assert.Equal(
            "--mode bare needs ANTHROPIC_API_KEY (or --claude-settings with an apiKeyHelper) and an Anthropic model: --bare never reads " +
            "OAuth or the keychain. With a subscription login (e.g. Enterprise), use --mode lean or leave --mode auto.",
            ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }
}
