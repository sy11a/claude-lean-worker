// Subcommands besides launching a worker: the worker's pre-tool hook, subscription quota, pricing a manual
// session, run statistics and the merged price book.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal static class Commands
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    private static Dictionary<string, string?> Flags(string[] args, params string[] booleans)
    {
        var d = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new LaunchException($"unexpected argument '{args[i]}'");
            if (booleans.Contains(args[i])) d[args[i]] = null;
            else d[args[i]] = i + 1 < args.Length ? args[++i] : throw new LaunchException($"{args[i]} needs a value");
        }
        return d;
    }

    /// <summary>PreToolUse hook: once the launcher has written wrapup.json, deny every tool call with its reason.
    /// A hook failure must never block the worker; the launcher's budget still applies.</summary>
    public static int Hook(string[] args)
    {
        try
        {
            Console.In.ReadToEnd();
            var runDir = Flags(args).GetValueOrDefault("--run-dir");
            if (runDir is null) return 0;
            File.AppendAllText(Path.Combine(runDir, "hook.log"), DateTimeOffset.Now.ToString("o") + Environment.NewLine);
            var marker = Path.Combine(runDir, "wrapup.json");
            if (!File.Exists(marker)) return 0;
            var reason = Json.Str(Json.ParseLenient(File.ReadAllText(marker)).AsObject(), "reason") ?? Meter.HandoffInstruction;
            Console.Out.Write(new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "deny", ["permissionDecisionReason"] = reason,
                },
            }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or LaunchException) { }
        return 0;
    }

    public static int Quota(string[] args)
    {
        var f = Flags(args, "--json");
        var prices = PriceBook.Load(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", f.GetValueOrDefault("--prices"));
        TimeSpan? maxAge = f.TryGetValue("--max-age", out var s) && s is not null ? TimeSpan.FromSeconds(int.Parse(s, Ic)) : null;
        var names = f.TryGetValue("--provider", out var p) && p is not null ? [p] : prices.QuotaProviders();
        if (names.Count == 0) throw new LaunchException("no provider in prices.json has a quota adapter");
        var readings = new JsonArray();
        foreach (var name in names)
        {
            var provider = prices.Provider(name);
            try
            {
                var q = LeanWorker.Quota.Read(provider, maxAge);
                if (f.ContainsKey("--json")) readings.Add(q.ToJson());
                else
                {
                    Console.Out.WriteLine($"{name}{(q.Level is null ? "" : $" ({q.Level})")}: {q.Line()}");
                    foreach (var w in q.Windows.Where(w => w.Detail is not null)) Console.Out.WriteLine($"  {w.Name}: {w.Detail}");
                    Console.Out.WriteLine($"  headroom for workers: {(LeanWorker.Quota.Headroom(provider, q).Ok ? "yes" : "no")} ({LeanWorker.Quota.Headroom(provider, q).Why})");
                }
            }
            catch (Exception ex) when (LeanWorker.Quota.IsReadFailure(ex) && ex is not LaunchException)
            {
                throw new LaunchException($"quota for {name}: {ex.Message}");
            }
        }
        if (f.ContainsKey("--json")) Console.Out.WriteLine(readings.ToJsonString(Json.Indented));
        return 0;
    }

    public static int Cost(string[] args)
    {
        var f = Flags(args);
        var prices = PriceBook.Load(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", f.GetValueOrDefault("--prices"));
        List<(string Provider, Usage Usage)> calls;
        string label;
        if (f.GetValueOrDefault("--claude") is { } claude)
        {
            var path = File.Exists(claude) ? claude : FindClaudeTranscript(claude);
            var provider = f.GetValueOrDefault("--provider") ?? "anthropic";
            var rt = new ClaudeRuntime();
            var byId = new Dictionary<string, Usage>();
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal) || Json.TryParseObject(line) is not { } obj) continue;
                if (rt.Parse(obj, new Outcome()) is { } u) byId[u.Id] = u; // the last record of a message wins
            }
            calls = byId.Values.Select(u => (provider, u)).ToList();
            label = path;
        }
        else if (f.GetValueOrDefault("--opencode") is { } session)
        {
            calls = OpencodeCalls(session);
            label = $"opencode session {session}";
        }
        else throw new LaunchException("cost needs --claude <session-id|file> or --opencode <session-id>");

        decimal total = 0;
        var notes = new HashSet<string>();
        var perModel = new Dictionary<string, (int Calls, decimal Cost, long Tokens)>();
        foreach (var (prov, u) in calls)
        {
            var c = prices.Resolve(prov, u.Model, out var note).Cost(u);
            if (note is not null) notes.Add(note);
            total += c;
            var key = $"{prov}/{u.Model}";
            var cur = perModel.GetValueOrDefault(key);
            perModel[key] = (cur.Calls + 1, cur.Cost + c, cur.Tokens + u.Context + u.Output + u.Reasoning);
        }
        Console.Out.WriteLine($"{label}: {calls.Count} API calls, ${total.ToString("0.0000", Ic)} at list price");
        foreach (var (key, v) in perModel.OrderByDescending(kv => kv.Value.Cost))
        {
            var billing = Launcher.Billing(prices.Provider(PriceBook.Split(key).Provider), "claude", false);
            Console.Out.WriteLine($"  {key}: {v.Calls} calls, {v.Tokens.ToString("N0", Ic)} tokens, ${v.Cost.ToString("0.0000", Ic)} ({billing})");
        }
        foreach (var n in notes) Console.Out.WriteLine($"  note: {n}");
        return 0;
    }

    private static string FindClaudeTranscript(string sessionId)
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var projects = Path.Combine(dir, "projects");
        var hit = Directory.Exists(projects) ? Directory.EnumerateFiles(projects, sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault() : null;
        return hit ?? throw new LaunchException($"no transcript {sessionId}.jsonl under {projects}");
    }

    private static List<(string, Usage)> OpencodeCalls(string session)
    {
        if (!Regex.IsMatch(session, "^ses_[A-Za-z0-9]+$")) throw new LaunchException($"not an opencode session id: {session}");
        var opencode = Launcher.FindOnPath("opencode") ?? throw new LaunchException("'opencode' is not on PATH.");
        var sql = "select id, json_extract(data,'$.providerID') as provider, json_extract(data,'$.modelID') as model, " +
                  $"json_extract(data,'$.tokens') as tokens from message where session_id='{session}' " +
                  "and json_extract(data,'$.role')='assistant' order by time_created";
        var psi = new ProcessStartInfo(opencode) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "db", sql, "--format", "json" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new LaunchException("could not start opencode");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new LaunchException($"opencode db failed: {p.StandardError.ReadToEnd().Trim()}");
        var rows = Json.ParseLenient(output) as JsonArray ?? [];
        var calls = new List<(string, Usage)>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Json.Str(row, "tokens") is not { } t || Json.ParseLenient(t) is not JsonObject tok) continue;
            var cache = tok["cache"] as JsonObject;
            calls.Add((Json.Str(row, "provider") ?? "", new Usage(Json.Str(row, "id") ?? "", Json.Str(row, "model") ?? "",
                Json.Num(tok["input"]), Json.Num(tok["output"]), Json.Num(tok["reasoning"]),
                Json.Num(cache?["read"]), Json.Num(cache?["write"]), 0)));
        }
        if (calls.Count == 0) throw new LaunchException($"no assistant messages with usage in opencode session {session}");
        return calls;
    }

    /// <summary>One line of <c>stats</c>: the runs of one profile on one model.</summary>
    internal sealed record StatsRow(string Profile, string Model, int Runs, int Success, int WrappedUp, int Escalations,
                                    decimal CostUsd, decimal? CostPerSuccessUsd, decimal? QuotaPctPerRun);

    internal static List<StatsRow> StatsRows(IEnumerable<JsonObject> runs) =>
        runs.GroupBy(r => (Json.Str(r, "profile") ?? "-", $"{Json.Str(r, "provider") ?? "anthropic"}/{Json.Str(r, "model")}"))
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal)
            .Select(g =>
            {
                var ok = g.Count(r => Json.Str(r, "status") == "success");
                var cost = g.Sum(r => Json.Dec(r, "total_cost_usd") ?? 0);
                var quota = g.Select(r => r["quota_used_pct"] as JsonObject).OfType<JsonObject>()
                             .Select(q => q.Select(kv => Json.Dec(q, kv.Key) ?? 0).DefaultIfEmpty(0).Max()).ToList();
                return new StatsRow(g.Key.Item1, g.Key.Item2, g.Count(), ok, g.Count(r => Json.Str(r, "status") == "wrapped-up"),
                    g.Count(r => Json.Str(r, "escalate_to") is not null), cost, ok > 0 ? cost / ok : null,
                    quota.Count > 0 ? quota.Average() : null);
            }).ToList();

    public static int Stats(string[] args)
    {
        var f = Flags(args, "--json");
        var path = Path.Combine(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", "runs.jsonl");
        if (!File.Exists(path)) throw new LaunchException($"no runs recorded yet ({path})");
        var since = f.GetValueOrDefault("--since") is { } s ? DateTimeOffset.Parse(s, Ic) : DateTimeOffset.MinValue;
        var runs = File.ReadLines(path).Select(Json.TryParseObject).OfType<JsonObject>()
            .Where(r => Json.Str(r, "timestamp") is { } t && DateTimeOffset.Parse(t, Ic) >= since).ToList();
        var rows = StatsRows(runs);
        if (f.ContainsKey("--json"))
        {
            var doc = new JsonObject
            {
                ["schema_version"] = Launcher.RunSchemaVersion,
                ["runs_file"] = Path.GetFullPath(path),
                ["since"] = since == DateTimeOffset.MinValue ? null : since.ToString("o"),
                ["cost_basis"] = "list price; list-price equivalent for subscriptions",
                ["groups"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["profile"] = r.Profile, ["model"] = r.Model, ["runs"] = r.Runs, ["success"] = r.Success,
                    ["wrapped_up"] = r.WrappedUp, ["escalations"] = r.Escalations,
                    ["cost_usd"] = decimal.Round(r.CostUsd, 6),
                    ["cost_per_success_usd"] = r.CostPerSuccessUsd is { } c ? decimal.Round(c, 6) : null,
                    ["quota_pct_per_run"] = r.QuotaPctPerRun is { } q ? decimal.Round(q, 2) : null,
                }).ToArray()),
            };
            Console.Out.WriteLine(doc.ToJsonString(Json.Indented));
            return 0;
        }
        Console.Out.WriteLine($"{"profile",-16} {"model",-34} {"runs",4} {"ok",4} {"wrap",4} {"esc",4} {"cost",9} {"$/success",9} {"quota%/run",10}");
        foreach (var r in rows)
            Console.Out.WriteLine($"{r.Profile,-16} {r.Model,-34} {r.Runs,4} {r.Success,4} {r.WrappedUp,4} {r.Escalations,4} {("$" + r.CostUsd.ToString("0.000", Ic)),9} " +
                                  $"{(r.CostPerSuccessUsd is { } c ? "$" + c.ToString("0.000", Ic) : "-"),9} {(r.QuotaPctPerRun is { } q ? q.ToString("0.0", Ic) : "-"),10}");
        Console.Out.WriteLine("cost = list price (list-price equivalent for subscriptions); esc = runs that offered the next model; quota%/run = largest window increase per run.");
        return 0;
    }

    public static int Prices(string[] args)
    {
        var f = Flags(args);
        var prices = PriceBook.Load(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", f.GetValueOrDefault("--prices"));
        Console.Out.WriteLine("sources (later ones override earlier):");
        foreach (var src in prices.Sources) Console.Out.WriteLine($"  {src}");
        Console.Out.WriteLine($"unknown models: {prices.UnknownModel}");
        foreach (var (key, asOf) in prices.Entries())
        {
            var stale = asOf is not null && DateTimeOffset.TryParse(asOf, Ic, DateTimeStyles.AssumeUniversal, out var d) && DateTimeOffset.Now - d > TimeSpan.FromDays(90);
            Console.Out.WriteLine($"  {key,-36} as of {asOf ?? "?"}{(stale ? "  <- older than 90 days, check it" : "")}");
        }
        return 0;
    }
}
