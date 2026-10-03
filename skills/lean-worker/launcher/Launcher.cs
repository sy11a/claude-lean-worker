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

internal static class Launcher
{
    private static readonly string[] _efforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] _permissionModes = ["acceptEdits", "dontAsk", "plan", "manual", "auto", "bypassPermissions"];
    private const string ContinuationHeading = "## Continuation (lean-worker)";
    public const int RunSchemaVersion = 1;

    public static async Task<int> RunAsync(Options o)
    {
        if (o.Help)
        {
            await Console.Out.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return 0;
        }

        // ---------- resolve profile ----------
        string runsRoot = o.RunsRoot ?? ".lean-worker";
        JsonObject? prevSummary = null;
        if (o.ContinueFrom is not null)
        {
            string prevSummaryPath = Path.Combine(o.ContinueFrom, "summary.json");
            if (!File.Exists(prevSummaryPath) || !File.Exists(Path.Combine(o.ContinueFrom, "task.md")))
            {
                throw new LaunchException($"--continue-from needs a finished run dir (summary.json + task.md): {o.ContinueFrom}");
            }

            prevSummary = Json.ParseLenient(await File.ReadAllTextAsync(prevSummaryPath).ConfigureAwait(false)).AsObject();
        }
        JsonObject? profile = null;
        string? profileName = o.Profile ?? Json.Str(prevSummary, "profile");
        string profilesPath = Path.Combine(runsRoot, "profiles.json");
        if (File.Exists(profilesPath))
        {
            JsonObject doc;
            try { doc = Json.ParseLenient(await File.ReadAllTextAsync(profilesPath).ConfigureAwait(false)).AsObject(); }
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

        PriceBook prices = PriceBook.Load(runsRoot, o.PricesFile ?? Json.Str(profile, "prices"));
        List<string> notes = [.. prices.Warnings];
        // An explicit runtime holds for every model in the chain; otherwise each model gets the runtime its provider allows.
        string? explicitRuntime = o.Runtime ?? Json.Str(profile, "runtime");
        if (explicitRuntime is not null)
        {
            _ = Runtimes.Get(explicitRuntime); // validates the name before any quota read
        }

        string runtimeName = explicitRuntime ?? "claude";
        List<string> chain;
        if (o.Model is not null)
        {
            chain = new List<string> { o.Model };
        }
        else if (profile?["model"] is JsonArray arr)
        {
            chain = new List<string>([.. arr.Select(x => x!.GetValue<string>())]);
        }
        else
        {
            chain = new List<string> { Json.Str(profile, "model") ?? "claude-sonnet-5" };
        }
        string effort = o.Effort ?? Json.Str(profile, "effort") ?? "medium";
        string? variant = o.Variant ?? Json.Str(profile, "variant");
        List<string> tools = o.Tools ?? Json.StrList(profile, "tools") ?? new List<string> { "Read", "Edit", "Write", "Glob", "Grep", "Bash" };
        List<string> allowed = o.AllowedTools.Count > 0 ? o.AllowedTools : Json.StrList(profile, "allowedTools") ?? new List<string>();
        // The paths the task may write; a continuation keeps its original run's scope.
        List<string>? writeScope = o.WriteScope.Count > 0 ? o.WriteScope
            : Json.StrList(profile, "writeScope") ?? Json.StrList(prevSummary, "write_scope");
        decimal budget = o.MaxBudgetUsd ?? Json.Dec(profile, "maxBudgetUsd") ?? 2m;
        // Share of the budget after which the wrap-up hook blocks tools; 0 turns it off.
        decimal wrapUpAt = o.WrapUpAt ?? Json.Dec(profile, "wrapUpAt") ?? 0.8m;
        if (wrapUpAt < 0 || wrapUpAt >= 1)
        {
            throw new LaunchException(string.Create(CultureInfo.InvariantCulture, $"invalid wrap-up share {wrapUpAt} (0 = off, else below 1)"));
        }

        string permissionMode = o.PermissionMode ?? Json.Str(profile, "permissionMode") ?? "acceptEdits";
        string? mcpConfig = o.McpConfig ?? Json.Str(profile, "mcpConfig");
        // Lean mode keeps the user's hooks, plugins and settings out of the worker unless the profile keeps them.
        bool keepHooks = o.KeepHooks || Json.Bool(profile, "keepHooks");
        // Worker calls follow each other within seconds, so the 5-minute cache is enough. A subscription login
        // would otherwise write the cache with the 1-hour TTL, which costs 2x base input instead of 1.25x.
        string cacheTtl = o.CacheTtl ?? Json.Str(profile, "cacheTtl") ?? "5m";
        if (cacheTtl is not ("5m" or "1h" or "default"))
        {
            throw new LaunchException($"invalid cache TTL '{cacheTtl}' (5m | 1h | default)");
        }

        if (!_efforts.Contains(effort, StringComparer.Ordinal))
        {
            throw new LaunchException($"invalid effort '{effort}'");
        }

        if (!_permissionModes.Contains(permissionMode, StringComparer.Ordinal))
        {
            throw new LaunchException($"invalid permission mode '{permissionMode}'");
        }

        // ---------- validate inputs ----------
        if (o.TaskFile is null && o.ContinueFrom is null)
        {
            throw new LaunchException("--task <file> or --continue-from <run-dir> is required");
        }

        if (o.TaskFile is not null && !File.Exists(o.TaskFile))
        {
            throw new LaunchException($"task file not found: {o.TaskFile}");
        }

        if (o.SystemFile is not null && !File.Exists(o.SystemFile))
        {
            throw new LaunchException($"system file not found: {o.SystemFile}");
        }

        if (mcpConfig is not null && !File.Exists(mcpConfig))
        {
            throw new LaunchException($"MCP config not found: {mcpConfig}");
        }

        if (o.ClaudeSettings is not null && !File.Exists(o.ClaudeSettings))
        {
            throw new LaunchException($"settings file not found: {o.ClaudeSettings}");
        }
        // An API key: the environment, or an apiKeyHelper in --claude-settings. Any other settings file is not a key.
        bool hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                     || (o.ClaudeSettings is not null && Json.ParseLenient(await File.ReadAllTextAsync(o.ClaudeSettings).ConfigureAwait(false))["apiKeyHelper"] is not null);

        // ---------- pick the model: the first in the chain with quota headroom ----------
        string provider = string.Empty;
        string model = string.Empty;
        string pickReason = string.Empty;
        QuotaReading? quotaBefore = null;
        for (int i = 0; i < chain.Count; i++)
        {
            (string? prov, string? mdl) = PriceBook.Split(chain[i]);
            Provider info = prices.Provider(prov);
            provider = prov ?? string.Empty;
            model = mdl ?? string.Empty;
            if (chain.Count <= 1)
            {
                pickReason = string.Empty;
            }
            else if (i == 0)
            {
                pickReason = "first in chain";
            }
            else
            {
                pickReason = "next in chain";
            }
            quotaBefore = null;
            runtimeName = explicitRuntime ?? DefaultRuntime(info);
            if (Billing(info, runtimeName, hasKey) is not "subscription" || info.Quota is null)
            {
                break;
            }

            try
            {
                quotaBefore = Quota.Read(info);
                (bool ok, string? why) = Quota.Headroom(info, quotaBefore);
                pickReason = why ?? string.Empty;
                if (ok)
                {
                    break;
                }

                if (i == chain.Count - 1)
                {
                    notes.Add($"every model in the chain is over its quota threshold; using the last ({why})");
                }
                else
                {
                    notes.Add($"skipped {chain[i]}: {why}");
                }
            }
            catch (Exception ex) when (Quota.IsReadFailure(ex))
            {
                notes.Add($"quota check for {prov} failed ({ex.Message}); assuming headroom");
                break;
            }
        }
        Provider providerInfo = prices.Provider(provider);
        IRuntime runtime = Runtimes.Get(runtimeName);
        if (explicitRuntime is null && runtimeName is not "claude")
        {
            notes.Add($"runtime {runtimeName}: provider {provider} has no Anthropic-compatible endpoint in the price book");
        }
        // What the model needs, whichever provider serves it: extra pre-approved commands and a note.
        ModelTraits? traits = prices.Traits(model);
        if (traits is not null)
        {
            List<string> added = new List<string>([.. traits.AllowedTools.Where(t => !allowed.Contains(t))]);
            if (added.Count > 0 && tools.Contains("Bash", StringComparer.OrdinalIgnoreCase))
            {
                allowed = new List<string>([.. allowed, .. added]);
            }
            else
            {
                added.Clear();
            }

            notes.Add($"model traits {traits.Key}: {(added.Count > 0 ? $"+{added.Count} allowed command pattern(s)" : "no allowlist change")}" +
                      (traits.Note is { Length: > 0 } tn ? $"; {tn}" : string.Empty));
        }
        _ = prices.Resolve(provider, model, out string? priceNote); // fails early under unknownModel: "error"
        if (priceNote is not null)
        {
            notes.Add(priceNote);
        }

        // ---------- mode ----------
        // claude runtime: bare = `claude --bare` (API key only, skips all hooks, so no wrap-up);
        // lean = the same minimal profile from flags, for a subscription login, a key, or another provider.
        string mode = "n/a";
        if (runtimeName == "claude")
        {
            switch (o.Mode)
            {
                case "auto":
                    mode = provider is not "anthropic" || wrapUpAt > 0 || !hasKey ? "lean" : "bare";
                    break;
                case "bare":
                case "lean":
                    mode = o.Mode;
                    break;
                default:
                    throw new LaunchException($"invalid mode '{o.Mode}' (auto | bare | lean)");
            }
            if (mode == "bare" && (!hasKey || provider is not "anthropic"))
            {
                throw new LaunchException("--mode bare needs ANTHROPIC_API_KEY (or --claude-settings with an apiKeyHelper) and an Anthropic model: --bare never reads " +
                                          "OAuth or the keychain. With a subscription login (e.g. Enterprise), use --mode lean or leave --mode auto.");
            }
        }
        bool wrapUp = wrapUpAt > 0 && mode is not "bare";
        if (wrapUpAt > 0 && mode == "bare")
        {
            notes.Add("bare mode skips hooks, so wrap-up is off; the budget is still enforced");
        }

        string billing = Billing(providerInfo, runtimeName, hasKey);

        // ---------- task ----------
        string taskText;
        string name;
        if (o.ContinueFrom is not null)
        {
            // Fresh worker, not a resumed session: the original task plus the previous worker's report.
            string prevTask = await File.ReadAllTextAsync(Path.Combine(o.ContinueFrom, "task.md"), Json.Utf8).ConfigureAwait(false);
            int cut = prevTask.IndexOf(ContinuationHeading, StringComparison.Ordinal);
            if (cut >= 0)
            {
                prevTask = prevTask[..cut];
            }

            string prevReport = File.Exists(Path.Combine(o.ContinueFrom, "report.md")) ? (await File.ReadAllTextAsync(Path.Combine(o.ContinueFrom, "report.md"), Json.Utf8).ConfigureAwait(false)).Trim() : string.Empty;
            string nl = Environment.NewLine;
            taskText = prevTask.TrimEnd() + nl + nl + ContinuationHeading + nl + nl +
                       $"A previous worker on this task stopped before finishing (status: {Json.Str(prevSummary, "status")}). Its report is below. " +
                       "Check the current state first (for example `git status` and `git diff --stat`) and do not redo finished work." +
                       nl + nl + (prevReport.Length > 0 ? prevReport : "(the previous worker left no report)") + nl;
            name = o.Name ?? Json.Str(prevSummary, "name") ?? "continuation";
        }
        else
        {
            string taskPath = Path.GetFullPath(o.TaskFile!);
            taskText = await File.ReadAllTextAsync(taskPath, Json.Utf8).ConfigureAwait(false);
            name = o.Name ?? new DirectoryInfo(Path.GetDirectoryName(taskPath)!).Name;
        }
        string safeName = new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')]);
        DateTimeOffset started = DateTimeOffset.Now;
        string runDir = Path.Combine(runsRoot, "runs", string.Create(CultureInfo.InvariantCulture, $"{started:yyyyMMdd-HHmmss}-{safeName}"));
        _ = Directory.CreateDirectory(runDir);
        await File.WriteAllTextAsync(Path.Combine(runDir, "task.md"), taskText, Json.Utf8).ConfigureAwait(false);

        // The worker's system notes = project notes + per-task system file. The path passed to the worker is
        // content-addressed, so it is the same in every run with identical content: opencode prints the file
        // path into the system prompt, and a per-run path breaks Anthropic's prompt cache for repeated tasks.
        List<string> parts = [];
        string projectNotes = Path.Combine(runsRoot, "project.md");
        if (!o.NoProjectNotes && File.Exists(projectNotes))
        {
            parts.Add(await File.ReadAllTextAsync(projectNotes, Json.Utf8).ConfigureAwait(false));
        }

        if (o.SystemFile is not null)
        {
            parts.Add(await File.ReadAllTextAsync(o.SystemFile, Json.Utf8).ConfigureAwait(false));
        }

        string? runSystem = null;
        if (parts.Count > 0)
        {
            string content = string.Join(Environment.NewLine + Environment.NewLine, parts);
            string sha12 = Sha12(content);
            runSystem = Path.GetFullPath(Path.Combine(runsRoot, "system", $"{sha12}.md"));
            AtomicWrite(runSystem, content);
            await File.WriteAllTextAsync(Path.Combine(runDir, "system.md"), content, Json.Utf8).ConfigureAwait(false);
        }

        // ---------- run ----------
        RunSpec spec = new(runDir, provider, model, effort, variant, tools, allowed, budget, wrapUp, permissionMode,
            mcpConfig, runSystem, o.ReplaceSystemPrompt, mode, cacheTtl, o.KeepClaudeMd, o.KeepMemory, keepHooks,
            !o.NoUserEnv, o.ClaudeSettings, providerInfo);
        Prepared prepared = runtime.Prepare(spec);
        await File.WriteAllTextAsync(Path.Combine(runDir, "command.txt"), prepared.CommandText, Json.Utf8).ConfigureAwait(false);

        WriteScope.Snapshot? treeBefore = await WriteScope.TakeAsync(Directory.GetCurrentDirectory(), runsRoot).ConfigureAwait(false);
        Meter meter = new(prices, provider, runDir, budget, wrapUp ? wrapUpAt : null, Meter.HandoffInstruction);
        Outcome outcome = new();
        string streamPath = Path.Combine(runDir, "stream.jsonl");
        string stderrPath = Path.Combine(runDir, "stderr.txt");
        (int exitCode, bool timedOut, bool capKilled) = await RunWorkerAsync(prepared, taskText, streamPath, stderrPath, o.TimeoutMinutes, runtime.Record, line =>
        {
            if (Json.TryParseObject(line) is not { } obj)
            {
                return false;
            }

            Usage? u = runtime.Parse(obj, outcome);
            return u is not null && meter.Add(u.Model.Length > 0 ? u : u with { Model = model });
        }).ConfigureAwait(false);
        runtime.Finish(outcome, exitCode);
        TimeSpan elapsed = DateTimeOffset.Now - started;
        WriteScope.Snapshot? treeAfter = treeBefore is null ? null : await WriteScope.TakeAsync(treeBefore.Root, runsRoot).ConfigureAwait(false);
        List<string>? changed = treeBefore is null || treeAfter is null ? null : WriteScope.Changed(treeBefore, treeAfter);
        List<string>? outOfScope = changed is null || writeScope is null ? null : [.. changed.Where(f => !WriteScope.InScope(f, writeScope))];
        notes.AddRange(meter.Notes);

        QuotaReading? quotaAfter = null;
        if (quotaBefore is not null)
        {
            try { quotaAfter = Quota.Read(providerInfo); }
            catch (Exception ex) when (Quota.IsReadFailure(ex)) { notes.Add($"quota re-read failed: {ex.Message}"); }
        }

        // ---------- outcome ----------
        List<Usage> calls = meter.Calls();
        string status;
        if (capKilled)
        {
            status = "budget-exceeded";
        }
        else if (!outcome.HasResult)
        {
            if (timedOut)
            {
                status = "timed-out";
            }
            else if (exitCode != 0)
            {
                status = "crashed";
            }
            else
            {
                status = "no-result";
            }
        }
        else if (outcome.IsError)
        {
            status = "error";
        }
        else
        {
            status = "success";
        }
        if (meter.WrappedUp && status is "success" or "error")
        {
            status = "wrapped-up";
        }

        string report = outcome.Report;
        JsonObject tok = new()
        {
            ["input"] = calls.Sum(c => c.Input),
            ["cache_write"] = calls.Sum(c => c.CacheWrite5m + c.CacheWrite1h),
            ["cache_read"] = calls.Sum(c => c.CacheRead),
            ["output"] = calls.Sum(c => c.Output + c.Reasoning),
            ["thinking"] = outcome.Thinking,
        };
        List<long> contexts = [.. calls.Select(c => c.Context)];
        long first = contexts.Count > 0 ? contexts[0] : 0;
        long peak = contexts.Count > 0 ? contexts.Max() : 0;
        Usage? firstCall = calls.Count > 0 ? calls[0] : null;
        double? firstCallCacheReadShare = null;
        if (firstCall?.Context > 0)
        {
            firstCallCacheReadShare = Math.Round((double)firstCall.CacheRead / firstCall.Context, 3, MidpointRounding.ToEven);
        }

        int hookChecks = File.Exists(Path.Combine(runDir, "hook.log")) ? File.ReadLines(Path.Combine(runDir, "hook.log")).Count() : 0;
        string? next = status is "wrapped-up" or "success" ? null : NextInChain(chain, provider, model);

        JsonObject summary = new()
        {
            // Bumped when a field changes meaning or is removed; added fields keep the version. Rows without it are 0.
            ["schema_version"] = RunSchemaVersion,
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
            ["total_cost_usd"] = decimal.Round(meter.Spent, 6, MidpointRounding.ToEven),
            ["reported_cost_usd"] = outcome.ReportedCost,
            ["budget_usd"] = budget,
            ["wrap_up_usd"] = wrapUp ? budget * wrapUpAt : null,
            ["wrapped_up"] = meter.WrappedUp,
            ["hook_checks"] = hookChecks,
            ["continued_from"] = o.ContinueFrom is null ? null : Path.GetFullPath(o.ContinueFrom),
            ["escalate_to"] = next,
            ["model_traits"] = traits?.Key,
            ["tokens"] = tok,
            ["context_first_call"] = first,
            ["first_call_cache_read"] = JsonValue.Create(firstCall?.CacheRead),
            ["first_call_cache_read_share"] = JsonValue.Create(firstCallCacheReadShare),
            ["context_peak"] = peak,
            ["permission_denials"] = outcome.Denials,
            ["write_scope"] = writeScope is null ? null : new JsonArray([.. writeScope.Select(p => (JsonNode)p)]),
            ["changed_files"] = changed is null ? null : new JsonArray([.. changed.Select(p => (JsonNode)p)]),
            ["out_of_scope"] = outOfScope is null ? null : new JsonArray([.. outOfScope.Select(p => (JsonNode)p)]),
            ["session_id"] = outcome.SessionId,
            ["quota_before"] = quotaBefore?.ToJson(),
            ["quota_after"] = quotaAfter?.ToJson(),
            ["quota_used_pct"] = QuotaDelta(quotaBefore, quotaAfter),
            ["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)n)]),
        };
        await File.WriteAllTextAsync(Path.Combine(runDir, "summary.json"), summary.ToJsonString(Json.Indented), Json.Utf8).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), report, Json.Utf8).ConfigureAwait(false);
        await File.AppendAllTextAsync(Path.Combine(runsRoot, "runs.jsonl"), summary.ToJsonString() + Environment.NewLine, Json.Utf8).ConfigureAwait(false);

        // ---------- print ----------
        CultureInfo ic = CultureInfo.InvariantCulture;
        Func<JsonNode?, string> N = n => Json.Num(n).ToString("N0", ic);
        TextWriter w = Console.Out;
        await w.WriteLineAsync("LEAN-WORKER RESULT").ConfigureAwait(false);
        await w.WriteLineAsync($"run:      {runDir}").ConfigureAwait(false);
        await w.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"status:   {status}  (subtype={outcome.Subtype}, reason={outcome.TerminalReason}, exit={exitCode})")).ConfigureAwait(false);
        string knob = runtimeName == "opencode" ? $"variant {variant ?? "default"}" : $"effort {effort}";
        await w.WriteLineAsync($"model:    {provider}/{model}{(pickReason.Length > 0 ? $" ({pickReason})" : string.Empty)}, {knob}, profile {profileName ?? "(none)"}").ConfigureAwait(false);
        await w.WriteLineAsync(runtimeName == "opencode"
            ? $"runtime:  opencode, {summary["hooks"]}"
            : $"runtime:  claude, mode {mode}, hooks {summary["hooks"]}, cache {cacheTtl}").ConfigureAwait(false);
        await w.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"work:     {outcome.Turns} turns, {calls.Count} API calls, {(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s")).ConfigureAwait(false);
        string costNote = billing == "subscription" ? "list-price equivalent; subscription, not billed" : "list price; metered";
        string reported = outcome.ReportedCost is { } rc && Math.Abs(rc - meter.Spent) > Math.Max(0.0005m, meter.Spent * 0.05m) ? string.Create(CultureInfo.InvariantCulture, $"; runtime reported ${rc:0.0000}") : string.Empty;
        await w.WriteLineAsync($"cost:     ${meter.Spent.ToString("0.0000", ic)} ({costNote}{reported})").ConfigureAwait(false);
        string wrapUpText = wrapUp ? string.Create(CultureInfo.InvariantCulture, $"wrap-up at ${(budget * wrapUpAt).ToString("0.####", ic)}{(meter.WrappedUp ? " (triggered)" : string.Empty)}, {hookChecks} hook checks") : "wrap-up off";
        await w.WriteLineAsync($"budget:   ${budget.ToString("0.####", ic)}, {wrapUpText}{(capKilled ? ", STOPPED at the budget" : string.Empty)}").ConfigureAwait(false);
        await w.WriteLineAsync($"tokens:   input {N(tok["input"])} | cache write {N(tok["cache_write"])} | cache read {N(tok["cache_read"])} | output {N(tok["output"])} (thinking {N(tok["thinking"])})").ConfigureAwait(false);
        string firstShareText = firstCallCacheReadShare is { } s ? $" (cache read {Math.Round(s * 100, MidpointRounding.ToEven).ToString(ic)}%)" : string.Empty;
        await w.WriteLineAsync($"context:  first call {first.ToString("N0", ic)}{firstShareText} | peak {peak.ToString("N0", ic)}").ConfigureAwait(false);
        if (quotaAfter is not null)
        {
            await w.WriteLineAsync($"quota:    {quotaAfter.Line(quotaBefore)}").ConfigureAwait(false);
        }

        if (changed is not null)
        {
            await w.WriteLineAsync($"files:    {changed.Count} changed in the working tree{(outOfScope is null ? " (no write scope given)" : $", {outOfScope.Count} outside the write scope")}").ConfigureAwait(false);
        }

        foreach (string n in notes)
        {
            await w.WriteLineAsync($"note:     {n}").ConfigureAwait(false);
        }

        if (meter.WrappedUp)
        {
            await w.WriteLineAsync($"continue: --continue-from \"{Path.GetFullPath(runDir)}\" (fresh worker, original task + this handoff; ask the operator first)").ConfigureAwait(false);
        }
        else if (next is not null)
        {
            await w.WriteLineAsync($"escalate: --continue-from \"{Path.GetFullPath(runDir)}\" --model {next} (next in the profile's chain; ask the operator first)").ConfigureAwait(false);
        }

        if (!meter.WrappedUp && outcome.Denials > 0)
        {
            await w.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"WARNING:  {outcome.Denials} permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.")).ConfigureAwait(false);
        }

        if (outOfScope is { Count: > 0 })
        {
            string extras = outOfScope.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $" (+{outOfScope.Count - 5} more, see summary.json)") : string.Empty;
            await w.WriteLineAsync($"WARNING:  {outOfScope.Count} file(s) changed outside the write scope: {string.Join(", ", outOfScope.Take(5))}{extras}. Check them before accepting the run.").ConfigureAwait(false);
        }

        await w.WriteLineAsync("--- worker report ---").ConfigureAwait(false);
        if (report.Length > o.ReportMaxChars)
        {
            await w.WriteLineAsync(report[..o.ReportMaxChars]).ConfigureAwait(false);
            await w.WriteLineAsync($"[truncated; full report: {Path.Combine(runDir, "report.md")}]").ConfigureAwait(false);
        }
        else if (report.Length > 0)
        {
            await w.WriteLineAsync(report).ConfigureAwait(false);
        }
        else
        {
            await w.WriteLineAsync("(no report text)").ConfigureAwait(false);
            FileInfo fi = new(stderrPath);
            if (fi.Exists && fi.Length > 0)
            {
                await w.WriteLineAsync("--- stderr (first 40 lines) ---").ConfigureAwait(false);
                foreach (string l in File.ReadLines(stderrPath, Json.Utf8).Take(40)) w.WriteLine(l);
            }
        }

        return status switch { "success" => 0, "wrapped-up" => 3, _ => 1 };
    }

    /// <summary>
    /// metered | subscription. Anthropic's "auto" is a subscription when the worker uses the login, not a key.
    /// </summary>
    public static string Billing(Provider p, string runtime, bool hasKey)
    {
        return p.Billing switch
        {
            "auto" when runtime == "claude" => hasKey ? "metered" : "subscription",
            "auto" => "metered",
            var b => b,
        };
    }

    private static string? NextInChain(List<string> chain, string provider, string model)
    {
        int i = chain.FindIndex(c => PriceBook.Split(c) == (provider, model));
        return i >= 0 && i + 1 < chain.Count ? chain[i + 1] : null;
    }

    private static JsonObject? QuotaDelta(QuotaReading? before, QuotaReading? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        JsonObject d = [];
        foreach (QuotaWindow w in after.Windows)
        {
            if (before.Windows.Find(b => b.Name == w.Name) is { } b)
            {
                d[w.Name] = w.Percent - b.Percent;
            }
        }

        return d;
    }

    /// <summary>
    /// Runs the worker, handing every stdout line to onLine; onLine returns true to stop the worker (hard cap).
    /// </summary>
    private static async Task<(int ExitCode, bool TimedOut, bool CapKilled)> RunWorkerAsync(Prepared prep, string stdin,
        string streamPath, string stderrPath, int timeoutMinutes, Func<string, bool> record, Func<string, bool> onLine)
    {
        ProcessStartInfo psi = new()
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Json.Utf8,
            StandardErrorEncoding = Json.Utf8,
            FileName = prep.Executable,
        };
        foreach (string arg in prep.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach ((string? k, string? v) in prep.Env)
        {
            if (v is null)
            {
                _ = psi.Environment.Remove(k);
            }
            else
            {
                psi.Environment[k] = v;
            }
        }

        using Process p = Process.Start(psi) ?? throw new LaunchException($"could not start {prep.Executable}");
        await using StreamWriter stream = new(streamPath, append: false, Json.Utf8);
        await using StreamWriter stderr = new(stderrPath, append: false, Json.Utf8);
        TaskCompletionSource outDone = new();
        TaskCompletionSource errDone = new();
        bool capKilled = false;
        bool closed = false;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { _ = outDone.TrySetResult(); return; }
            bool stop;
            try
            {
                lock (stream) { if (closed) { return; } if (record(e.Data)) { stream.WriteLine(e.Data); stream.Flush(); } }
                stop = onLine(e.Data);
            }
            catch (Exception ex)
            {
                // Metering broke: fail closed rather than let the worker spend unmetered.
                lock (stream)
                {
                    if (!closed)
                    {
                        File.AppendAllText(stderrPath + ".launcher", $"metering failed, worker stopped: {ex}{Environment.NewLine}");
                    }
                }
                stop = true;
            }
            if (stop && !capKilled)
            {
                capKilled = true;
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                _ = errDone.TrySetResult();
            }
            else
            {
                lock (stream) { if (!closed) { stderr.WriteLine(e.Data); } }
            }
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();

        bool timedOut = !p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes));
        if (timedOut)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await p.WaitForExitAsync().ConfigureAwait(false);
        }
        Task.WaitAll([outDone.Task, errDone.Task], TimeSpan.FromSeconds(30));
        lock (stream)
        {
            closed = true;
        }

        return (timedOut ? -1 : p.ExitCode, timedOut, capKilled);
    }

    /// <summary>
    /// The claude runtime reaches Anthropic and any provider with an Anthropic-compatible endpoint; others need opencode.
    /// </summary>
    internal static string DefaultRuntime(Provider p) => p.Name == "anthropic" || p.AnthropicBaseUrl is not null ? "claude" : "opencode";

    public static string? FindOnPath(string command)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim('"'), command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// First 12 hex chars of the SHA-256 of the UTF-8 bytes; the path is the same in every run of identical content.
    /// </summary>
    private static string Sha12(string content)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Json.Utf8.GetBytes(content));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically (temp + move), only if the file is absent.
    /// </summary>
    private static void AtomicWrite(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, Json.Utf8);
            try { File.Move(temp, path); }
            catch (IOException) { /* lost the race; the other writer's content has the same hash */ }
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}