// LeanWorker: runs one well-scoped task in a separate, minimal-context Claude Code process
// (`claude -p --bare`) and prints a compact report with token usage and cost.
//
// Settings resolve in this order: command-line option > profile in <runs-root>/profiles.json > built-in default.
// Project notes (<runs-root>/project.md) are given to every worker unless --no-project-notes;
// a per-task --system file is appended after them.
//
// Exit codes: 0 = worker finished without error, 1 = worker reported an error, 2 = launcher failed.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class Program
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] PermissionModes = ["acceptEdits", "dontAsk", "plan", "manual", "auto", "bypassPermissions"];

    private static int Main(string[] args)
    {
        try
        {
            return Run(Options.Parse(args));
        }
        catch (LaunchException ex)
        {
            Console.Out.WriteLine($"LEAN-WORKER LAUNCH FAILED: {ex.Message}");
            return 2;
        }
    }

    private static int Run(Options o)
    {
        if (o.Help)
        {
            Console.Out.WriteLine(Options.Usage);
            return 0;
        }

        // ---------- resolve profile ----------
        var runsRoot = o.RunsRoot ?? ".lean-worker";
        JsonObject? profile = null;
        var profileName = o.Profile;
        var profilesPath = Path.Combine(runsRoot, "profiles.json");
        if (File.Exists(profilesPath))
        {
            JsonObject doc;
            try { doc = JsonNode.Parse(File.ReadAllText(profilesPath, Utf8))!.AsObject(); }
            catch (Exception ex) { throw new LaunchException($"profiles.json is not valid JSON: {ex.Message}"); }
            profileName ??= doc["defaultProfile"]?.GetValue<string>();
            if (profileName is not null)
            {
                profile = doc["profiles"]?[profileName]?.AsObject()
                    ?? throw new LaunchException($"profile '{profileName}' not found in {profilesPath}");
            }
        }
        else if (profileName is not null)
        {
            throw new LaunchException($"--profile given but {profilesPath} does not exist");
        }

        var model = o.Model ?? Str(profile, "model") ?? "claude-sonnet-5";
        var effort = o.Effort ?? Str(profile, "effort") ?? "medium";
        var tools = o.Tools ?? StrList(profile, "tools") ?? ["Read", "Edit", "Write", "Glob", "Grep", "Bash"];
        var allowed = o.AllowedTools.Count > 0 ? o.AllowedTools : StrList(profile, "allowedTools") ?? [];
        var budget = o.MaxBudgetUsd ?? Dec(profile, "maxBudgetUsd") ?? 2m;
        var permissionMode = o.PermissionMode ?? Str(profile, "permissionMode") ?? "acceptEdits";
        var mcpConfig = o.McpConfig ?? Str(profile, "mcpConfig");
        if (!Efforts.Contains(effort)) throw new LaunchException($"invalid effort '{effort}'");
        if (!PermissionModes.Contains(permissionMode)) throw new LaunchException($"invalid permission mode '{permissionMode}'");

        // ---------- validate inputs ----------
        if (o.TaskFile is null) throw new LaunchException("--task <file> is required");
        if (!File.Exists(o.TaskFile)) throw new LaunchException($"task file not found: {o.TaskFile}");
        if (o.SystemFile is not null && !File.Exists(o.SystemFile)) throw new LaunchException($"system file not found: {o.SystemFile}");
        if (mcpConfig is not null && !File.Exists(mcpConfig)) throw new LaunchException($"MCP config not found: {mcpConfig}");
        var claude = FindOnPath("claude") ?? throw new LaunchException("'claude' is not on PATH.");
        if (o.ClaudeSettings is not null && !File.Exists(o.ClaudeSettings)) throw new LaunchException($"settings file not found: {o.ClaudeSettings}");
        if (!o.NoBare && o.ClaudeSettings is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            throw new LaunchException("ANTHROPIC_API_KEY is not set. --bare reads only the API key (never OAuth/keychain). " +
                                      "Set the key, pass --claude-settings with an apiKeyHelper, or pass --no-bare to run without --bare.");
        }

        var taskPath = Path.GetFullPath(o.TaskFile);
        var name = o.Name ?? new DirectoryInfo(Path.GetDirectoryName(taskPath)!).Name;
        var safeName = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray());
        var started = DateTimeOffset.Now;
        var runDir = Path.Combine(runsRoot, "runs", $"{started:yyyyMMdd-HHmmss}-{safeName}");
        Directory.CreateDirectory(runDir);
        File.Copy(taskPath, Path.Combine(runDir, "task.md"), overwrite: true);

        // The worker's system notes = project notes + per-task system file, saved into the run dir.
        var parts = new List<string>();
        var projectNotes = Path.Combine(runsRoot, "project.md");
        if (!o.NoProjectNotes && File.Exists(projectNotes)) parts.Add(File.ReadAllText(projectNotes, Utf8));
        if (o.SystemFile is not null) parts.Add(File.ReadAllText(o.SystemFile, Utf8));
        string? runSystem = null;
        if (parts.Count > 0)
        {
            runSystem = Path.GetFullPath(Path.Combine(runDir, "system.md"));
            File.WriteAllText(runSystem, string.Join(Environment.NewLine + Environment.NewLine, parts), Utf8);
        }

        // ---------- build arguments ----------
        var a = new List<string> { "-p" };
        if (!o.NoBare) a.Add("--bare");
        a.AddRange(["--model", model, "--effort", effort]);
        if (runSystem is not null) a.AddRange([o.ReplaceSystemPrompt ? "--system-prompt-file" : "--append-system-prompt-file", runSystem]);
        a.AddRange(["--tools", string.Join(",", tools)]);
        if (allowed.Count > 0) { a.Add("--allowedTools"); a.AddRange(allowed); }
        if (o.ClaudeSettings is not null) a.AddRange(["--settings", Path.GetFullPath(o.ClaudeSettings)]);
        a.Add("--strict-mcp-config");
        if (mcpConfig is not null) a.AddRange(["--mcp-config", Path.GetFullPath(mcpConfig)]);
        a.AddRange(["--permission-mode", permissionMode,
                    "--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture),
                    "--no-session-persistence", "--output-format", "stream-json", "--verbose"]);
        File.WriteAllText(Path.Combine(runDir, "command.txt"), "claude " + string.Join(" ", a.Select(Quote)), Utf8);

        // ---------- run ----------
        var streamPath = Path.Combine(runDir, "stream.jsonl");
        var stderrPath = Path.Combine(runDir, "stderr.txt");
        var (exitCode, timedOut) = RunClaude(claude, a, File.ReadAllText(taskPath, Utf8), streamPath, stderrPath, o.TimeoutMinutes);
        var elapsed = DateTimeOffset.Now - started;

        // ---------- parse ----------
        var seen = new HashSet<string>();
        var contexts = new List<long>();
        JsonObject? result = null;
        foreach (var line in File.ReadLines(streamPath, Utf8))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonObject obj;
            try { obj = JsonNode.Parse(line)!.AsObject(); } catch (JsonException) { continue; }
            var type = obj["type"]?.GetValue<string>();
            if (type == "assistant" && obj["message"]?["usage"] is JsonObject u)
            {
                var id = obj["message"]?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                if (!seen.Add(id)) continue; // one row per content block; usage repeats
                contexts.Add(Num(u["input_tokens"]) + Num(u["cache_read_input_tokens"]) + Num(u["cache_creation_input_tokens"]));
            }
            else if (type == "result")
            {
                result = obj;
            }
        }

        var usage = result?["usage"];
        var status = result is null ? (timedOut ? "timed-out" : exitCode != 0 ? "crashed" : "no-result")
                   : result["is_error"]?.GetValue<bool>() == true ? "error" : "success";
        var report = result?["result"]?.GetValue<string>() ?? "";
        var tok = new JsonObject
        {
            ["input"] = Num(usage?["input_tokens"]),
            ["cache_write"] = Num(usage?["cache_creation_input_tokens"]),
            ["cache_read"] = Num(usage?["cache_read_input_tokens"]),
            ["output"] = Num(usage?["output_tokens"]),
            ["thinking"] = Num(usage?["output_tokens_details"]?["thinking_tokens"]),
        };
        var cost = result?["total_cost_usd"]?.GetValue<double>() ?? 0;
        var denials = result?["permission_denials"] is JsonArray d ? d.Count : 0;
        var first = contexts.Count > 0 ? contexts[0] : 0;
        var peak = contexts.Count > 0 ? contexts.Max() : 0;

        var summary = new JsonObject
        {
            ["timestamp"] = started.ToString("o"),
            ["name"] = name,
            ["run_dir"] = runDir,
            ["profile"] = profileName,
            ["model"] = model,
            ["effort"] = effort,
            ["bare"] = !o.NoBare,
            ["status"] = status,
            ["subtype"] = result?["subtype"]?.GetValue<string>(),
            ["terminal_reason"] = result?["terminal_reason"]?.GetValue<string>(),
            ["exit_code"] = exitCode,
            ["num_turns"] = Num(result?["num_turns"]),
            ["api_calls"] = contexts.Count,
            ["duration_ms"] = (long)elapsed.TotalMilliseconds,
            ["total_cost_usd"] = cost,
            ["tokens"] = tok,
            ["context_first_call"] = first,
            ["context_peak"] = peak,
            ["permission_denials"] = denials,
            ["session_id"] = result?["session_id"]?.GetValue<string>(),
        };
        File.WriteAllText(Path.Combine(runDir, "summary.json"), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
        File.WriteAllText(Path.Combine(runDir, "report.md"), report, Utf8);
        File.AppendAllText(Path.Combine(runsRoot, "runs.jsonl"), summary.ToJsonString() + Environment.NewLine, Utf8);

        // ---------- print ----------
        var ic = CultureInfo.InvariantCulture;
        string N(JsonNode? n) => Num(n).ToString("N0", ic);
        var w = Console.Out;
        w.WriteLine("LEAN-WORKER RESULT");
        w.WriteLine($"run:      {runDir}");
        w.WriteLine($"status:   {status}  (subtype={summary["subtype"]}, reason={summary["terminal_reason"]}, exit={exitCode})");
        w.WriteLine($"model:    {model}, effort {effort}, profile {profileName ?? "(none)"}, bare={!o.NoBare}");
        w.WriteLine($"work:     {Num(result?["num_turns"])} turns, {contexts.Count} API calls, {(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s");
        w.WriteLine($"cost:     ${cost.ToString("0.0000", ic)} (list price reported by Claude Code)");
        w.WriteLine($"tokens:   input {N(tok["input"])} | cache write {N(tok["cache_write"])} | cache read {N(tok["cache_read"])} | output {N(tok["output"])} (thinking {N(tok["thinking"])})");
        w.WriteLine($"context:  first call {first.ToString("N0", ic)} | peak {peak.ToString("N0", ic)}");
        if (denials > 0) w.WriteLine($"WARNING:  {denials} permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.");
        w.WriteLine("--- worker report ---");
        if (report.Length > o.ReportMaxChars)
        {
            w.WriteLine(report[..o.ReportMaxChars]);
            w.WriteLine($"[truncated; full report: {Path.Combine(runDir, "report.md")}]");
        }
        else if (report.Length > 0)
        {
            w.WriteLine(report);
        }
        else
        {
            w.WriteLine("(no report text)");
            if (new FileInfo(stderrPath) is { Exists: true, Length: > 0 })
            {
                w.WriteLine("--- stderr (first 40 lines) ---");
                foreach (var l in File.ReadLines(stderrPath, Utf8).Take(40)) w.WriteLine(l);
            }
        }

        return status == "success" ? 0 : 1;
    }

    private static (int ExitCode, bool TimedOut) RunClaude(string claude, List<string> args, string stdin,
        string streamPath, string stderrPath, int timeoutMinutes)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        // An npm-installed claude on Windows is a .cmd shim, which must be started through cmd.exe.
        if (OperatingSystem.IsWindows() && claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(claude);
        }
        else
        {
            psi.FileName = claude;
        }
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi) ?? throw new LaunchException("could not start claude");
        using var stream = new StreamWriter(streamPath, false, Utf8);
        using var stderr = new StreamWriter(stderrPath, false, Utf8);
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) outDone.TrySetResult(); else lock (stream) stream.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) errDone.TrySetResult(); else lock (stderr) stderr.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();

        var timedOut = !p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes));
        if (timedOut)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
        }
        Task.WaitAll([outDone.Task, errDone.Task], TimeSpan.FromSeconds(30));
        return (timedOut ? -1 : p.ExitCode, timedOut);
    }

    private static string? FindOnPath(string command)
    {
        var exts = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat" }
            : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim('"'), command + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string Quote(string s) => s.Contains(' ') || s.Contains('(') ? $"\"{s}\"" : s;

    private static long Num(JsonNode? n) => n is not JsonValue v ? 0
        : v.TryGetValue(out long l) ? l
        : v.TryGetValue(out double d) ? (long)d
        : 0;
    private static string? Str(JsonObject? o, string key) => o?[key]?.GetValue<string>();
    private static decimal? Dec(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue(out decimal d) ? d : null;
    private static List<string>? StrList(JsonObject? o, string key) =>
        o?[key] is JsonArray arr ? arr.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList() : null;
}

internal sealed class LaunchException(string message) : Exception(message);

internal sealed class Options
{
    public const string Usage = """
        LeanWorker: run one task in a minimal-context `claude -p --bare` worker.

          --task <file>              task prompt (required)
          --profile <name>           profile from <runs-root>/profiles.json (default: its defaultProfile)
          --system <file>            per-task notes, appended after <runs-root>/project.md
          --name <name>              run name (default: the task file's folder name)
          --model <id>               override the profile's model
          --effort <level>           low | medium | high | xhigh | max
          --tools <A,B,C>            built-in tools available to the worker
          --allow <pattern>          pre-approved tool pattern, repeatable, e.g. --allow "Bash(git diff:*)"
          --mcp-config <file>        MCP servers for this run only (always --strict-mcp-config)
          --max-budget-usd <n>       spend cap for the run
          --permission-mode <mode>   acceptEdits | dontAsk | plan | manual | auto | bypassPermissions
          --runs-root <dir>          default .lean-worker
          --claude-settings <file>   passed to claude as --settings (e.g. an apiKeyHelper for --bare)
          --timeout-minutes <n>      kill the worker after n minutes (default 60)
          --report-max-chars <n>     truncate the printed report (default 6000)
          --no-project-notes         do not give the worker <runs-root>/project.md
          --replace-system-prompt    replace Claude Code's system prompt instead of appending
          --no-bare                  same lean profile without --bare (e.g. no API key)
        """;

    public string? TaskFile, Profile, SystemFile, Name, Model, Effort, McpConfig, PermissionMode, RunsRoot, ClaudeSettings;
    public List<string>? Tools;
    public List<string> AllowedTools = [];
    public decimal? MaxBudgetUsd;
    public int TimeoutMinutes = 60, ReportMaxChars = 6000;
    public bool NoProjectNotes, ReplaceSystemPrompt, NoBare, Help;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new LaunchException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--task": o.TaskFile = Next(); break;
                case "--profile": o.Profile = Next(); break;
                case "--system": o.SystemFile = Next(); break;
                case "--name": o.Name = Next(); break;
                case "--model": o.Model = Next(); break;
                case "--effort": o.Effort = Next(); break;
                case "--tools": o.Tools = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--allow": o.AllowedTools.Add(Next()); break;
                case "--mcp-config": o.McpConfig = Next(); break;
                case "--max-budget-usd": o.MaxBudgetUsd = decimal.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--permission-mode": o.PermissionMode = Next(); break;
                case "--runs-root": o.RunsRoot = Next(); break;
                case "--claude-settings": o.ClaudeSettings = Next(); break;
                case "--timeout-minutes": o.TimeoutMinutes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--report-max-chars": o.ReportMaxChars = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-project-notes": o.NoProjectNotes = true; break;
                case "--replace-system-prompt": o.ReplaceSystemPrompt = true; break;
                case "--no-bare": o.NoBare = true; break;
                case "-h" or "--help": o.Help = true; break;
                default: throw new LaunchException($"unknown option '{args[i]}' (see --help)");
            }
        }
        return o;
    }
}
