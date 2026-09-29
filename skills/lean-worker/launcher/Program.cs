// LeanWorker: runs one well-scoped task in a separate, minimal-context worker process (Claude Code `claude -p`
// or opencode `opencode run`) and prints a compact report with token usage and cost.
//
// Settings resolve in this order: command-line option > profile in <runs-root>/profiles.json > built-in default.
// Project notes (<runs-root>/project.md) are given to every worker unless --no-project-notes;
// a per-task --system file is appended after them.
//
// Spend is metered live from the worker's stream with the price book (prices.json). Past the wrap-up share of the
// budget a pre-tool hook blocks every tool call, so the worker's last message is a handoff; at the budget the
// launcher stops the worker.
//
// Exit codes: 0 = worker finished without error, 1 = worker reported an error, 2 = launcher failed,
// 3 = worker wrapped up near its budget and left a handoff (continue with --continue-from <run-dir>).

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "hook" => Commands.Hook(args[1..]),
                "quota" => Commands.Quota(args[1..]),
                "cost" => Commands.Cost(args[1..]),
                "stats" => Commands.Stats(args[1..]),
                "prices" => Commands.Prices(args[1..]),
                _ => Launcher.Run(Options.Parse(args)),
            };
        }
        catch (Exception ex) when (ex is LaunchException or System.Text.Json.JsonException or FormatException or OverflowException
                                       or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            Console.Out.WriteLine($"LEAN-WORKER LAUNCH FAILED: {ex.Message}");
            return 2;
        }
    }
}

internal static class Launcher
{
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] PermissionModes = ["acceptEdits", "dontAsk", "plan", "manual", "auto", "bypassPermissions"];
    private const string ContinuationHeading = "## Continuation (lean-worker)";

    public static int Run(Options o)
    {
        if (o.Help)
        {
            Console.Out.WriteLine(Options.Usage);
            return 0;
        }

        // ---------- resolve profile ----------
        var runsRoot = o.RunsRoot ?? ".lean-worker";
        JsonObject? prevSummary = null;
        if (o.ContinueFrom is not null)
        {
            var prevSummaryPath = Path.Combine(o.ContinueFrom, "summary.json");
            if (!File.Exists(prevSummaryPath) || !File.Exists(Path.Combine(o.ContinueFrom, "task.md")))
                throw new LaunchException($"--continue-from needs a finished run dir (summary.json + task.md): {o.ContinueFrom}");
            prevSummary = Json.ParseLenient(File.ReadAllText(prevSummaryPath)).AsObject();
        }
        JsonObject? profile = null;
        var profileName = o.Profile ?? Json.Str(prevSummary, "profile");
        var profilesPath = Path.Combine(runsRoot, "profiles.json");
        if (File.Exists(profilesPath))
        {
            JsonObject doc;
            try { doc = Json.ParseLenient(File.ReadAllText(profilesPath)).AsObject(); }
            catch (Exception ex) { throw new LaunchException($"profiles.json is not valid JSON: {ex.Message}"); }
            profileName ??= Json.Str(doc, "defaultProfile");
            if (profileName is not null)
            {
                profile = doc["profiles"]?[profileName] as JsonObject
                    ?? throw new LaunchException($"profile '{profileName}' not found in {profilesPath}");
            }
        }
        else if (profileName is not null && o.ContinueFrom is null)
        {
            throw new LaunchException($"--profile given but {profilesPath} does not exist");
        }

        var prices = PriceBook.Load(runsRoot, o.PricesFile ?? Json.Str(profile, "prices"));
        var notes = new List<string>(prices.Warnings);
        var runtimeName = o.Runtime ?? Json.Str(profile, "runtime") ?? "claude";
        var runtime = Runtimes.Get(runtimeName);
        var chain = o.Model is not null ? [o.Model]
            : profile?["model"] is JsonArray arr ? arr.Select(x => x!.GetValue<string>()).ToList()
            : [Json.Str(profile, "model") ?? "claude-sonnet-5"];
        var effort = o.Effort ?? Json.Str(profile, "effort") ?? "medium";
        var variant = o.Variant ?? Json.Str(profile, "variant");
        var tools = o.Tools ?? Json.StrList(profile, "tools") ?? ["Read", "Edit", "Write", "Glob", "Grep", "Bash"];
        var allowed = o.AllowedTools.Count > 0 ? o.AllowedTools : Json.StrList(profile, "allowedTools") ?? [];
        // The paths the task may write; a continuation keeps its original run's scope.
        var writeScope = o.WriteScope.Count > 0 ? o.WriteScope
            : Json.StrList(profile, "writeScope") ?? Json.StrList(prevSummary, "write_scope");
        var budget = o.MaxBudgetUsd ?? Json.Dec(profile, "maxBudgetUsd") ?? 2m;
        // Share of the budget after which the wrap-up hook blocks tools; 0 turns it off.
        var wrapUpAt = o.WrapUpAt ?? Json.Dec(profile, "wrapUpAt") ?? 0.8m;
        if (wrapUpAt is < 0 or >= 1) throw new LaunchException($"invalid wrap-up share {wrapUpAt} (0 = off, else below 1)");
        var permissionMode = o.PermissionMode ?? Json.Str(profile, "permissionMode") ?? "acceptEdits";
        var mcpConfig = o.McpConfig ?? Json.Str(profile, "mcpConfig");
        // Lean mode keeps the user's hooks, plugins and settings out of the worker unless the profile keeps them.
        var keepHooks = o.KeepHooks || Json.Bool(profile, "keepHooks");
        // Worker calls follow each other within seconds, so the 5-minute cache is enough. A subscription login
        // would otherwise write the cache with the 1-hour TTL, which costs 2x base input instead of 1.25x.
        var cacheTtl = o.CacheTtl ?? Json.Str(profile, "cacheTtl") ?? "5m";
        if (cacheTtl is not ("5m" or "1h" or "default")) throw new LaunchException($"invalid cache TTL '{cacheTtl}' (5m | 1h | default)");
        if (!Efforts.Contains(effort)) throw new LaunchException($"invalid effort '{effort}'");
        if (!PermissionModes.Contains(permissionMode)) throw new LaunchException($"invalid permission mode '{permissionMode}'");

        // ---------- validate inputs ----------
        if (o.TaskFile is null && o.ContinueFrom is null) throw new LaunchException("--task <file> or --continue-from <run-dir> is required");
        if (o.TaskFile is not null && !File.Exists(o.TaskFile)) throw new LaunchException($"task file not found: {o.TaskFile}");
        if (o.SystemFile is not null && !File.Exists(o.SystemFile)) throw new LaunchException($"system file not found: {o.SystemFile}");
        if (mcpConfig is not null && !File.Exists(mcpConfig)) throw new LaunchException($"MCP config not found: {mcpConfig}");
        if (o.ClaudeSettings is not null && !File.Exists(o.ClaudeSettings)) throw new LaunchException($"settings file not found: {o.ClaudeSettings}");
        // An API key: the environment, or an apiKeyHelper in --claude-settings. Any other settings file is not a key.
        var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                     || (o.ClaudeSettings is not null && Json.ParseLenient(File.ReadAllText(o.ClaudeSettings))["apiKeyHelper"] is not null);

        // ---------- pick the model: the first in the chain with quota headroom ----------
        string provider = "", model = "", pickReason = "";
        QuotaReading? quotaBefore = null;
        for (var i = 0; i < chain.Count; i++)
        {
            var (prov, mdl) = PriceBook.Split(chain[i]);
            var info = prices.Provider(prov);
            (provider, model, pickReason, quotaBefore) = (prov, mdl, chain.Count <= 1 ? "" : i == 0 ? "first in chain" : "next in chain", null);
            if (Billing(info, runtimeName, hasKey) != "subscription" || info.Quota is null) break;
            try
            {
                quotaBefore = Quota.Read(info);
                var (ok, why) = Quota.Headroom(info, quotaBefore);
                pickReason = why;
                if (ok) break;
                if (i == chain.Count - 1) notes.Add($"every model in the chain is over its quota threshold; using the last ({why})");
                else notes.Add($"skipped {chain[i]}: {why}");
            }
            catch (Exception ex) when (Quota.IsReadFailure(ex))
            {
                notes.Add($"quota check for {prov} failed ({ex.Message}); assuming headroom");
                break;
            }
        }
        var providerInfo = prices.Provider(provider);
        prices.Resolve(provider, model, out var priceNote); // fails early under unknownModel: "error"
        if (priceNote is not null) notes.Add(priceNote);

        // ---------- mode ----------
        // claude runtime: bare = `claude --bare` (API key only, skips all hooks, so no wrap-up);
        // lean = the same minimal profile from flags, for a subscription login, a key, or another provider.
        var mode = "n/a";
        if (runtimeName == "claude")
        {
            mode = o.Mode switch
            {
                "auto" => provider != "anthropic" || wrapUpAt > 0 || !hasKey ? "lean" : "bare",
                "bare" or "lean" => o.Mode,
                _ => throw new LaunchException($"invalid mode '{o.Mode}' (auto | bare | lean)"),
            };
            if (mode == "bare" && (!hasKey || provider != "anthropic"))
            {
                throw new LaunchException("--mode bare needs ANTHROPIC_API_KEY (or --claude-settings with an apiKeyHelper) and an Anthropic model: --bare never reads " +
                                          "OAuth or the keychain. With a subscription login (e.g. Enterprise), use --mode lean or leave --mode auto.");
            }
        }
        var wrapUp = wrapUpAt > 0 && mode != "bare";
        if (wrapUpAt > 0 && mode == "bare") notes.Add("bare mode skips hooks, so wrap-up is off; the budget is still enforced");
        var billing = Billing(providerInfo, runtimeName, hasKey);

        // ---------- task ----------
        string taskText, name;
        if (o.ContinueFrom is not null)
        {
            // Fresh worker, not a resumed session: the original task plus the previous worker's report.
            var prevTask = File.ReadAllText(Path.Combine(o.ContinueFrom, "task.md"), Json.Utf8);
            var cut = prevTask.IndexOf(ContinuationHeading, StringComparison.Ordinal);
            if (cut >= 0) prevTask = prevTask[..cut];
            var prevReport = File.Exists(Path.Combine(o.ContinueFrom, "report.md")) ? File.ReadAllText(Path.Combine(o.ContinueFrom, "report.md"), Json.Utf8).Trim() : "";
            var nl = Environment.NewLine;
            taskText = prevTask.TrimEnd() + nl + nl + ContinuationHeading + nl + nl +
                       $"A previous worker on this task stopped before finishing (status: {Json.Str(prevSummary, "status")}). Its report is below. " +
                       "Check the current state first (for example `git status` and `git diff --stat`) and do not redo finished work." +
                       nl + nl + (prevReport.Length > 0 ? prevReport : "(the previous worker left no report)") + nl;
            name = o.Name ?? Json.Str(prevSummary, "name") ?? "continuation";
        }
        else
        {
            var taskPath = Path.GetFullPath(o.TaskFile!);
            taskText = File.ReadAllText(taskPath, Json.Utf8);
            name = o.Name ?? new DirectoryInfo(Path.GetDirectoryName(taskPath)!).Name;
        }
        var safeName = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray());
        var started = DateTimeOffset.Now;
        var runDir = Path.Combine(runsRoot, "runs", $"{started:yyyyMMdd-HHmmss}-{safeName}");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "task.md"), taskText, Json.Utf8);

        // The worker's system notes = project notes + per-task system file, saved into the run dir.
        var parts = new List<string>();
        var projectNotes = Path.Combine(runsRoot, "project.md");
        if (!o.NoProjectNotes && File.Exists(projectNotes)) parts.Add(File.ReadAllText(projectNotes, Json.Utf8));
        if (o.SystemFile is not null) parts.Add(File.ReadAllText(o.SystemFile, Json.Utf8));
        string? runSystem = null;
        if (parts.Count > 0)
        {
            runSystem = Path.GetFullPath(Path.Combine(runDir, "system.md"));
            File.WriteAllText(runSystem, string.Join(Environment.NewLine + Environment.NewLine, parts), Json.Utf8);
        }

        // ---------- run ----------
        var spec = new RunSpec(runDir, provider, model, effort, variant, tools, allowed, budget, wrapUp, permissionMode,
            mcpConfig, runSystem, o.ReplaceSystemPrompt, mode, cacheTtl, o.KeepClaudeMd, o.KeepMemory, keepHooks,
            !o.NoUserEnv, o.ClaudeSettings, providerInfo);
        var prepared = runtime.Prepare(spec);
        File.WriteAllText(Path.Combine(runDir, "command.txt"), prepared.CommandText, Json.Utf8);

        var treeBefore = WriteScope.Take(Directory.GetCurrentDirectory(), runsRoot);
        var meter = new Meter(prices, provider, runDir, budget, wrapUp ? wrapUpAt : null, Meter.HandoffInstruction);
        var outcome = new Outcome();
        var streamPath = Path.Combine(runDir, "stream.jsonl");
        var stderrPath = Path.Combine(runDir, "stderr.txt");
        var (exitCode, timedOut, capKilled) = RunWorker(prepared, taskText, streamPath, stderrPath, o.TimeoutMinutes, runtime.Record, line =>
        {
            if (Json.TryParseObject(line) is not { } obj) return false;
            var u = runtime.Parse(obj, outcome);
            return u is not null && meter.Add(u.Model.Length > 0 ? u : u with { Model = model });
        });
        runtime.Finish(outcome, exitCode);
        var elapsed = DateTimeOffset.Now - started;
        var treeAfter = treeBefore is null ? null : WriteScope.Take(treeBefore.Root, runsRoot);
        var changed = treeBefore is null || treeAfter is null ? null : WriteScope.Changed(treeBefore, treeAfter);
        var outOfScope = changed is null || writeScope is null ? null : changed.Where(f => !WriteScope.InScope(f, writeScope)).ToList();
        notes.AddRange(meter.Notes);

        QuotaReading? quotaAfter = null;
        if (quotaBefore is not null)
        {
            try { quotaAfter = Quota.Read(providerInfo); }
            catch (Exception ex) when (Quota.IsReadFailure(ex)) { notes.Add($"quota re-read failed: {ex.Message}"); }
        }

        // ---------- outcome ----------
        var calls = meter.Calls();
        var status = capKilled ? "budget-exceeded"
                   : !outcome.HasResult ? (timedOut ? "timed-out" : exitCode != 0 ? "crashed" : "no-result")
                   : outcome.IsError ? "error" : "success";
        if (meter.WrappedUp && status is "success" or "error") status = "wrapped-up";
        var report = outcome.Report;
        var tok = new JsonObject
        {
            ["input"] = calls.Sum(c => c.Input),
            ["cache_write"] = calls.Sum(c => c.CacheWrite5m + c.CacheWrite1h),
            ["cache_read"] = calls.Sum(c => c.CacheRead),
            ["output"] = calls.Sum(c => c.Output + c.Reasoning),
            ["thinking"] = outcome.Thinking,
        };
        var contexts = calls.Select(c => c.Context).ToList();
        var first = contexts.Count > 0 ? contexts[0] : 0;
        var peak = contexts.Count > 0 ? contexts.Max() : 0;
        var hookChecks = File.Exists(Path.Combine(runDir, "hook.log")) ? File.ReadLines(Path.Combine(runDir, "hook.log")).Count() : 0;
        var next = status is "wrapped-up" or "success" ? null : NextInChain(chain, provider, model);

        var summary = new JsonObject
        {
            ["timestamp"] = started.ToString("o"),
            ["name"] = name,
            ["run_dir"] = runDir,
            ["profile"] = profileName,
            ["runtime"] = runtimeName,
            ["provider"] = provider,
            ["model"] = model,
            ["model_reason"] = pickReason.Length > 0 ? pickReason : null,
            ["effort"] = effort,
            ["mode"] = mode,
            ["billing"] = billing,
            ["cache_ttl"] = cacheTtl,
            ["hooks"] = runtimeName == "opencode" ? "user plugins off (clean config)"
                        : mode == "bare" ? "off (bare)" : keepHooks ? "on" : "off (managed hooks still run)",
            ["status"] = status,
            ["subtype"] = outcome.Subtype,
            ["terminal_reason"] = outcome.TerminalReason,
            ["exit_code"] = exitCode,
            ["num_turns"] = outcome.Turns,
            ["api_calls"] = calls.Count,
            ["duration_ms"] = (long)elapsed.TotalMilliseconds,
            ["total_cost_usd"] = decimal.Round(meter.Spent, 6),
            ["reported_cost_usd"] = outcome.ReportedCost,
            ["budget_usd"] = budget,
            ["wrap_up_usd"] = wrapUp ? budget * wrapUpAt : null,
            ["wrapped_up"] = meter.WrappedUp,
            ["hook_checks"] = hookChecks,
            ["continued_from"] = o.ContinueFrom is null ? null : Path.GetFullPath(o.ContinueFrom),
            ["tokens"] = tok,
            ["context_first_call"] = first,
            ["context_peak"] = peak,
            ["permission_denials"] = outcome.Denials,
            ["write_scope"] = writeScope is null ? null : new JsonArray(writeScope.Select(p => (JsonNode)p).ToArray()),
            ["changed_files"] = changed is null ? null : new JsonArray(changed.Select(p => (JsonNode)p).ToArray()),
            ["out_of_scope"] = outOfScope is null ? null : new JsonArray(outOfScope.Select(p => (JsonNode)p).ToArray()),
            ["session_id"] = outcome.SessionId,
            ["quota_before"] = quotaBefore?.ToJson(),
            ["quota_after"] = quotaAfter?.ToJson(),
            ["quota_used_pct"] = QuotaDelta(quotaBefore, quotaAfter),
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray()),
        };
        File.WriteAllText(Path.Combine(runDir, "summary.json"), summary.ToJsonString(Json.Indented), Json.Utf8);
        File.WriteAllText(Path.Combine(runDir, "report.md"), report, Json.Utf8);
        File.AppendAllText(Path.Combine(runsRoot, "runs.jsonl"), summary.ToJsonString() + Environment.NewLine, Json.Utf8);

        // ---------- print ----------
        var ic = CultureInfo.InvariantCulture;
        string N(JsonNode? n) => Json.Num(n).ToString("N0", ic);
        var w = Console.Out;
        w.WriteLine("LEAN-WORKER RESULT");
        w.WriteLine($"run:      {runDir}");
        w.WriteLine($"status:   {status}  (subtype={outcome.Subtype}, reason={outcome.TerminalReason}, exit={exitCode})");
        var knob = runtimeName == "opencode" ? $"variant {variant ?? "default"}" : $"effort {effort}";
        w.WriteLine($"model:    {provider}/{model}{(pickReason.Length > 0 ? $" ({pickReason})" : "")}, {knob}, profile {profileName ?? "(none)"}");
        w.WriteLine(runtimeName == "opencode"
            ? $"runtime:  opencode, {summary["hooks"]}"
            : $"runtime:  claude, mode {mode}, hooks {summary["hooks"]}, cache {cacheTtl}");
        w.WriteLine($"work:     {outcome.Turns} turns, {calls.Count} API calls, {(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s");
        var costNote = billing == "subscription" ? "list-price equivalent; subscription, not billed" : "list price; metered";
        var reported = outcome.ReportedCost is { } rc && Math.Abs(rc - meter.Spent) > Math.Max(0.0005m, meter.Spent * 0.05m) ? $"; runtime reported ${rc:0.0000}" : "";
        w.WriteLine($"cost:     ${meter.Spent.ToString("0.0000", ic)} ({costNote}{reported})");
        var wrapUpText = wrapUp ? $"wrap-up at ${(budget * wrapUpAt).ToString("0.####", ic)}{(meter.WrappedUp ? " (triggered)" : "")}, {hookChecks} hook checks" : "wrap-up off";
        w.WriteLine($"budget:   ${budget.ToString("0.####", ic)}, {wrapUpText}{(capKilled ? ", STOPPED at the budget" : "")}");
        w.WriteLine($"tokens:   input {N(tok["input"])} | cache write {N(tok["cache_write"])} | cache read {N(tok["cache_read"])} | output {N(tok["output"])} (thinking {N(tok["thinking"])})");
        w.WriteLine($"context:  first call {first.ToString("N0", ic)} | peak {peak.ToString("N0", ic)}");
        if (quotaAfter is not null) w.WriteLine($"quota:    {quotaAfter.Line(quotaBefore)}");
        if (changed is not null)
            w.WriteLine($"files:    {changed.Count} changed in the working tree{(outOfScope is null ? " (no write scope given)" : $", {outOfScope.Count} outside the write scope")}");
        foreach (var n in notes) w.WriteLine($"note:     {n}");
        if (meter.WrappedUp) w.WriteLine($"continue: --continue-from \"{Path.GetFullPath(runDir)}\" (fresh worker, original task + this handoff; ask the operator first)");
        else if (next is not null) w.WriteLine($"escalate: --continue-from \"{Path.GetFullPath(runDir)}\" --model {next} (next in the profile's chain; ask the operator first)");
        if (!meter.WrappedUp && outcome.Denials > 0) w.WriteLine($"WARNING:  {outcome.Denials} permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.");
        if (outOfScope is { Count: > 0 })
            w.WriteLine($"WARNING:  {outOfScope.Count} file(s) changed outside the write scope: {string.Join(", ", outOfScope.Take(5))}" +
                        $"{(outOfScope.Count > 5 ? $" (+{outOfScope.Count - 5} more, see summary.json)" : "")}. Check them before accepting the run.");
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
                foreach (var l in File.ReadLines(stderrPath, Json.Utf8).Take(40)) w.WriteLine(l);
            }
        }

        return status switch { "success" => 0, "wrapped-up" => 3, _ => 1 };
    }

    /// <summary>metered | subscription. Anthropic's "auto" is a subscription when the worker uses the login, not a key.</summary>
    public static string Billing(Provider p, string runtime, bool hasKey) => p.Billing switch
    {
        "auto" when runtime == "claude" => hasKey ? "metered" : "subscription",
        "auto" => "metered",
        var b => b,
    };

    private static string? NextInChain(List<string> chain, string provider, string model)
    {
        var i = chain.FindIndex(c => PriceBook.Split(c) == (provider, model));
        return i >= 0 && i + 1 < chain.Count ? chain[i + 1] : null;
    }

    private static JsonObject? QuotaDelta(QuotaReading? before, QuotaReading? after)
    {
        if (before is null || after is null) return null;
        var d = new JsonObject();
        foreach (var w in after.Windows)
            if (before.Windows.FirstOrDefault(b => b.Name == w.Name) is { } b) d[w.Name] = w.Percent - b.Percent;
        return d;
    }

    /// <summary>Runs the worker, handing every stdout line to onLine; onLine returns true to stop the worker (hard cap).</summary>
    private static (int ExitCode, bool TimedOut, bool CapKilled) RunWorker(Prepared prep, string stdin,
        string streamPath, string stderrPath, int timeoutMinutes, Func<string, bool> record, Func<string, bool> onLine)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Json.Utf8,
            StandardErrorEncoding = Json.Utf8,
        };
        // An npm-installed CLI on Windows is a .cmd shim, which must be started through cmd.exe.
        if (OperatingSystem.IsWindows() && prep.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(prep.Executable);
        }
        else
        {
            psi.FileName = prep.Executable;
        }
        foreach (var arg in prep.Args) psi.ArgumentList.Add(arg);
        foreach (var (k, v) in prep.Env)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }

        using var p = Process.Start(psi) ?? throw new LaunchException($"could not start {prep.Executable}");
        using var stream = new StreamWriter(streamPath, false, Json.Utf8);
        using var stderr = new StreamWriter(stderrPath, false, Json.Utf8);
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();
        var capKilled = false;
        var closed = false;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            bool stop;
            try
            {
                lock (stream) { if (closed) return; if (record(e.Data)) { stream.WriteLine(e.Data); stream.Flush(); } }
                stop = onLine(e.Data);
            }
            catch (Exception ex)
            {
                // Metering broke: fail closed rather than let the worker spend unmetered.
                lock (stream) { if (!closed) File.AppendAllText(stderrPath + ".launcher", $"metering failed, worker stopped: {ex}{Environment.NewLine}"); }
                stop = true;
            }
            if (stop && !capKilled)
            {
                capKilled = true;
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) errDone.TrySetResult(); else lock (stream) { if (!closed) stderr.WriteLine(e.Data); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();

        var timedOut = !p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes));
        if (timedOut)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.WaitForExit();
        }
        Task.WaitAll([outDone.Task, errDone.Task], TimeSpan.FromSeconds(30));
        lock (stream) closed = true;
        return (timedOut ? -1 : p.ExitCode, timedOut, capKilled);
    }

    public static string? FindOnPath(string command)
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
}

internal sealed class LaunchException(string message) : Exception(message);
