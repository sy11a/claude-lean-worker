// Runtimes: how a worker is started and how its output stream is read. Claude Code (`claude -p`) and
// opencode (`opencode run`) are supported; both stream JSON lines the launcher meters live.

using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>Everything a runtime needs to start one worker, resolved from options, profile and defaults.</summary>
internal sealed record RunSpec(
    string RunDir, string Provider, string Model, string Effort, string? Variant,
    List<string> Tools, List<string> Allowed, decimal Budget, bool WrapUp, string PermissionMode,
    string? McpConfig, string? SystemFile, bool ReplaceSystemPrompt, string Mode, string CacheTtl,
    bool KeepClaudeMd, bool KeepMemory, bool KeepHooks, bool KeepUserEnv, string? ClaudeSettings, Provider ProviderInfo);

internal sealed record Prepared(string Executable, List<string> Args, Dictionary<string, string?> Env, string CommandText);

/// <summary>What the stream said besides usage: the report, the outcome and bookkeeping.</summary>
internal sealed class Outcome
{
    public bool HasResult, IsError;
    public string Report = "";
    public string? SessionId, Subtype, TerminalReason;
    public decimal? ReportedCost;
    public long Turns, Denials, Thinking;
    public string? LastMessageId;
    public readonly List<string> Texts = [];
}

internal interface IRuntime
{
    string Name { get; }
    Prepared Prepare(RunSpec s);
    /// <summary>Reads one stream line; returns the API call's usage if the line carries one.</summary>
    Usage? Parse(JsonObject line, Outcome o);
    /// <summary>Settles the outcome after the process exits.</summary>
    void Finish(Outcome o, int exitCode);
    /// <summary>Whether a stream line is kept in stream.jsonl (token-by-token deltas are not).</summary>
    bool Record(string line) => true;
}

internal static class Runtimes
{
    public static IRuntime Get(string name) => name switch
    {
        "claude" => new ClaudeRuntime(),
        "opencode" => new OpencodeRuntime(),
        _ => throw new LaunchException($"invalid runtime '{name}' (claude | opencode)"),
    };

    /// <summary>The command that runs this launcher's `hook` subcommand, for the worker's pre-tool hook.</summary>
    public static string HookCommand(string runDir)
    {
        var self = Environment.ProcessPath ?? throw new LaunchException("cannot locate the launcher executable");
        var dll = Path.Combine(AppContext.BaseDirectory, "LeanWorker.dll");
        var exe = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"{Q(self)} {Q(dll)}"
            : Q(self);
        return $"{exe} hook --run-dir {Q(Path.GetFullPath(runDir))}";
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>The API key for a non-Anthropic provider: its keyEnv variable, else opencode's stored login.</summary>
    public static string ProviderKey(Provider p)
    {
        if (p.KeyEnv is { } env && Environment.GetEnvironmentVariable(env) is { Length: > 0 } fromEnv) return fromEnv;
        var auth = Path.Combine(DataHome(), "opencode", "auth.json");
        if (File.Exists(auth) && Json.ParseLenient(File.ReadAllText(auth))[p.Name] is JsonObject entry
            && Json.Str(entry, "key") is { Length: > 0 } key) return key;
        throw new LaunchException($"no API key for provider '{p.Name}': set {p.KeyEnv ?? "its keyEnv variable"} or log in with `opencode auth login`");
    }

    public static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static string Quote(string s) => s.Length == 0 ? "\"\"" : s.Contains(' ') || s.Contains('(') || s.Contains('"') ? $"\"{s}\"" : s;
}

internal sealed class ClaudeRuntime : IRuntime
{
    public string Name => "claude";

    public Prepared Prepare(RunSpec s)
    {
        var claude = Launcher.FindOnPath("claude") ?? throw new LaunchException("'claude' is not on PATH.");
        var env = new Dictionary<string, string?>();
        if (s.CacheTtl != "default") env["CLAUDE_CODE_PROMPT_CACHE_TTL"] = s.CacheTtl;
        if (s.Provider != "anthropic")
        {
            // Another provider's Anthropic-compatible endpoint. The key goes through the environment, never to disk.
            var url = s.ProviderInfo.AnthropicBaseUrl
                      ?? throw new LaunchException($"provider '{s.Provider}' has no anthropicBaseUrl in prices.json; use --runtime opencode");
            env["ANTHROPIC_BASE_URL"] = url;
            env["ANTHROPIC_AUTH_TOKEN"] = Runtimes.ProviderKey(s.ProviderInfo);
            env["ANTHROPIC_API_KEY"] = null;
            foreach (var tier in new[] { "HAIKU", "SONNET", "OPUS" }) env[$"ANTHROPIC_DEFAULT_{tier}_MODEL"] = s.Model;
        }

        var a = new List<string> { "-p" };
        if (s.Mode == "bare") a.Add("--bare");
        a.AddRange(["--model", s.Model, "--effort", s.Effort]);
        if (s.SystemFile is not null) a.AddRange([s.ReplaceSystemPrompt ? "--system-prompt-file" : "--append-system-prompt-file", s.SystemFile]);
        a.AddRange(["--tools", string.Join(",", s.Tools)]);
        if (s.Allowed.Count > 0) { a.Add("--allowedTools"); a.AddRange(s.Allowed); }
        if (s.Mode == "lean")
        {
            // Without --bare Claude Code would load CLAUDE.md / AGENTS.md / rules, the user's settings, hooks and
            // plugins. --setting-sources "" loads no user/project/local settings at all (managed settings and
            // --settings still apply), so personal hooks and plugins stay out while the injected hook runs.
            var settings = s.ClaudeSettings is not null
                ? Json.ParseLenient(File.ReadAllText(s.ClaudeSettings)).AsObject()
                : new JsonObject();
            if (!s.KeepClaudeMd)
                settings["claudeMdExcludes"] = new JsonArray("**/CLAUDE.md", "**/CLAUDE.local.md", "**/AGENTS.md", "**/.claude/rules/**");
            if (!s.KeepMemory) settings["autoMemoryEnabled"] = false;
            settings["outputStyle"] = "default"; // a personal output style would otherwise shape the worker's prompt
            if (!s.KeepHooks && s.KeepUserEnv && UserSettingsEnv() is { } userEnv)
            {
                // Settings sources are off, so carry the user's env block (proxies, for example) over explicitly,
                // through the process environment: it may hold tokens, which must not land in the run dir.
                // What the launcher sets itself (another provider's endpoint and key) wins.
                foreach (var (k, v) in userEnv)
                    if (!env.ContainsKey(k) && v is JsonValue jv && jv.TryGetValue(out string? value)) env[k] = value;
            }
            if (s.WrapUp)
            {
                if (settings["hooks"] is not JsonObject hooks) settings["hooks"] = hooks = new JsonObject();
                if (hooks["PreToolUse"] is not JsonArray pre) hooks["PreToolUse"] = pre = new JsonArray();
                pre.Add(new JsonObject
                {
                    ["matcher"] = ".*",
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Runtimes.HookCommand(s.RunDir) }),
                });
            }
            var settingsPath = Path.GetFullPath(Path.Combine(s.RunDir, "settings.json"));
            File.WriteAllText(settingsPath, settings.ToJsonString(Json.Indented), Json.Utf8);
            a.AddRange(["--settings", settingsPath, "--disable-slash-commands"]);
            if (!s.KeepHooks) a.AddRange(["--setting-sources", ""]);
        }
        else if (s.ClaudeSettings is not null)
        {
            a.AddRange(["--settings", Path.GetFullPath(s.ClaudeSettings)]);
        }
        a.Add("--strict-mcp-config");
        if (s.McpConfig is not null) a.AddRange(["--mcp-config", Path.GetFullPath(s.McpConfig)]);
        a.AddRange(["--permission-mode", s.PermissionMode]);
        // Claude Code prices only Claude models correctly, so its own cap is a second net for them only;
        // the launcher's meter enforces the budget for every model.
        if (s.Provider == "anthropic") a.AddRange(["--max-budget-usd", s.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        // Partial messages carry each API call's final output tokens (message_delta); the assistant events only
        // repeat the count from the start of the message, which undercounts output.
        a.AddRange(["--no-session-persistence", "--output-format", "stream-json", "--verbose", "--include-partial-messages"]);
        return new Prepared(claude, a, env, "claude " + string.Join(" ", a.Select(Runtimes.Quote)));
    }

    private static JsonObject? UserSettingsEnv()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var path = Path.Combine(dir, "settings.json");
        try { return File.Exists(path) ? Json.ParseLenient(File.ReadAllText(path))["env"] as JsonObject : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private readonly Dictionary<string, Usage> _calls = new();
    private string? _streaming;

    public bool Record(string line) => !line.StartsWith("{\"type\":\"stream_event\"", StringComparison.Ordinal)
                                       || line.Contains("\"message_start\"", StringComparison.Ordinal)
                                       || line.Contains("\"message_delta\"", StringComparison.Ordinal);

    public static Usage FromApiUsage(string id, string model, JsonObject u)
    {
        var cc = u["cache_creation"] as JsonObject;
        var w1h = Json.Num(cc?["ephemeral_1h_input_tokens"]);
        var w5m = cc?["ephemeral_5m_input_tokens"] is not null
            ? Json.Num(cc["ephemeral_5m_input_tokens"])
            : Json.Num(u["cache_creation_input_tokens"]) - w1h;
        return new Usage(id, model, Json.Num(u["input_tokens"]), Json.Num(u["output_tokens"]), 0,
            Json.Num(u["cache_read_input_tokens"]), Math.Max(0, w5m), w1h);
    }

    // One API call is reported several times (message_start, one assistant event per content block, message_delta).
    // Anthropic has final input and cache counts at the start and the final output count only in message_delta;
    // other providers' Anthropic-compatible endpoints (z.ai) send zeros at the start and everything in the delta.
    // Every count only grows, so the largest value of each field is the final one.
    private Usage Merge(Usage u)
    {
        if (_calls.TryGetValue(u.Id, out var p))
            u = new Usage(u.Id, u.Model.Length > 0 ? u.Model : p.Model, Math.Max(u.Input, p.Input), Math.Max(u.Output, p.Output), 0,
                Math.Max(u.CacheRead, p.CacheRead), Math.Max(u.CacheWrite5m, p.CacheWrite5m), Math.Max(u.CacheWrite1h, p.CacheWrite1h));
        return _calls[u.Id] = u;
    }

    public Usage? Parse(JsonObject obj, Outcome o)
    {
        var type = Json.Str(obj, "type");
        if (type == "stream_event" && obj["event"] is JsonObject ev)
        {
            var evType = Json.Str(ev, "type");
            if (evType == "message_start" && ev["message"] is JsonObject start && start["usage"] is JsonObject su)
            {
                _streaming = Json.Str(start, "id") ?? Guid.NewGuid().ToString();
                return Merge(FromApiUsage(_streaming, Json.Str(start, "model") ?? "", su));
            }
            if (evType == "message_delta" && _streaming is not null && ev["usage"] is JsonObject du)
                return Merge(FromApiUsage(_streaming, "", du));
            return null;
        }
        if (type == "assistant" && obj["message"] is JsonObject msg && msg["usage"] is JsonObject u)
            return Merge(FromApiUsage(Json.Str(msg, "id") ?? Guid.NewGuid().ToString(), Json.Str(msg, "model") ?? "", u));
        if (type == "result")
        {
            o.HasResult = true;
            o.IsError = Json.Bool(obj, "is_error");
            o.Report = Json.Str(obj, "result") ?? "";
            o.SessionId = Json.Str(obj, "session_id");
            o.Subtype = Json.Str(obj, "subtype");
            o.TerminalReason = Json.Str(obj, "terminal_reason");
            o.ReportedCost = Json.Dec(obj, "total_cost_usd");
            o.Turns = Json.Num(obj["num_turns"]);
            o.Denials = obj["permission_denials"] is JsonArray d ? d.Count : 0;
            o.Thinking = Json.Num(obj["usage"]?["output_tokens_details"]?["thinking_tokens"]);
        }
        return null;
    }

    public void Finish(Outcome o, int exitCode) { }
}

internal sealed class OpencodeRuntime : IRuntime
{
    public string Name => "opencode";

    // Claude Code tool names (the profile vocabulary) -> opencode permission keys.
    private static readonly Dictionary<string, string[]> ToolKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Read"] = ["read", "list"], ["Edit"] = ["edit"], ["Write"] = ["edit"], ["Glob"] = ["glob"], ["Grep"] = ["grep"],
        ["Bash"] = ["bash"], ["WebFetch"] = ["webfetch"], ["WebSearch"] = ["websearch"],
    };

    public Prepared Prepare(RunSpec s)
    {
        var opencode = Launcher.FindOnPath("opencode") ?? throw new LaunchException("'opencode' is not on PATH.");
        if (s.McpConfig is not null) throw new LaunchException("--mcp-config is supported by the claude runtime only");

        // A clean config home: no global instructions, plugins, skills or MCP servers reach the worker.
        // Logins stay in the data directory, which is not moved.
        var xdg = Path.GetFullPath(Path.Combine(s.RunDir, "opencode-config"));
        var pluginDir = Path.Combine(xdg, "opencode", "plugins");
        Directory.CreateDirectory(pluginDir);
        var plugin = Path.Combine(AppContext.BaseDirectory, "opencode-plugin.ts");
        if (!File.Exists(plugin)) throw new LaunchException($"opencode plugin not found next to the launcher: {plugin}");
        File.Copy(plugin, Path.Combine(pluginDir, "lean-worker.ts"), overwrite: true);

        var permission = new JsonObject { ["*"] = "deny" };
        foreach (var tool in s.Tools)
        {
            if (!ToolKeys.TryGetValue(tool, out var keys)) throw new LaunchException($"tool '{tool}' has no opencode equivalent");
            foreach (var k in keys) permission[k] = "allow";
        }
        if (s.Tools.Contains("Bash", StringComparer.OrdinalIgnoreCase) && s.PermissionMode != "bypassPermissions")
        {
            // Bash runs only the pre-approved patterns; anything else would prompt, and `opencode run` rejects prompts.
            var bash = new JsonObject { ["*"] = "ask" };
            foreach (var pattern in s.Allowed)
                if (pattern.StartsWith("Bash(", StringComparison.Ordinal) && pattern.EndsWith(')'))
                    bash[pattern[5..^1].Replace(":*", "*")] = "allow";
            permission["bash"] = bash;
        }

        var config = new JsonObject
        {
            ["autoupdate"] = false,
            ["share"] = "disabled",
            ["permission"] = permission,
            ["agent"] = new JsonObject
            {
                ["lean-worker"] = new JsonObject { ["mode"] = "primary", ["model"] = $"{s.Provider}/{s.Model}", ["permission"] = permission.DeepClone() },
            },
        };
        if (s.SystemFile is not null) config["instructions"] = new JsonArray(s.SystemFile);
        if (UserProviderBlock(s.Provider) is { } provider) config["provider"] = new JsonObject { [s.Provider] = provider };

        var env = new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = xdg,
            // The plugin puts the user's own value back for the worker's shell commands (git, gh and others).
            ["LEAN_WORKER_XDG_CONFIG_HOME"] = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "",
            ["LEAN_WORKER_RUN_DIR"] = Path.GetFullPath(s.RunDir),
            ["LEAN_WORKER_WRAPUP"] = s.WrapUp ? "1" : "0",
            ["OPENCODE_CONFIG_CONTENT"] = config.ToJsonString(),
            ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1",
            ["OPENCODE_DISABLE_CLAUDE_CODE_SKILLS"] = "1",
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "1",
        };
        if (!s.KeepClaudeMd)
        {
            env["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1";
            env["OPENCODE_DISABLE_CLAUDE_CODE"] = "1";
        }
        // The config may carry a provider's key, so the record keeps it without the provider block.
        var recorded = (JsonObject)config.DeepClone();
        if (recorded.ContainsKey("provider")) recorded["provider"] = "(copied from the user's opencode config; not recorded)";
        File.WriteAllText(Path.Combine(s.RunDir, "opencode-config.json"), recorded.ToJsonString(Json.Indented), Json.Utf8);

        var a = new List<string> { "run", "--format", "json", "--agent", "lean-worker", "-m", $"{s.Provider}/{s.Model}" };
        if (s.Variant is not null) a.AddRange(["--variant", s.Variant]);
        return new Prepared(opencode, a, env, "opencode " + string.Join(" ", a.Select(Runtimes.Quote)));
    }

    /// <summary>A custom provider defined in the user's opencode config, carried into the clean config home.</summary>
    private static JsonNode? UserProviderBlock(string provider)
    {
        var dir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        foreach (var name in new[] { "opencode.jsonc", "opencode.json", "config.json" })
        {
            var path = Path.Combine(dir, "opencode", name);
            if (!File.Exists(path)) continue;
            try { if (Json.ParseLenient(File.ReadAllText(path))["provider"]?[provider] is JsonNode n) return n.DeepClone(); }
            catch (System.Text.Json.JsonException) { }
        }
        return null;
    }

    public Usage? Parse(JsonObject obj, Outcome o)
    {
        o.SessionId ??= Json.Str(obj, "sessionID");
        var part = obj["part"] as JsonObject;
        switch (Json.Str(obj, "type"))
        {
            case "text" when part is not null:
                var messageId = Json.Str(part, "messageID");
                if (messageId != o.LastMessageId) { o.Texts.Clear(); o.LastMessageId = messageId; }
                if (Json.Str(part, "text") is { Length: > 0 } t) o.Texts.Add(t);
                break;
            case "error":
                o.IsError = true;
                o.Texts.Add(obj["error"]?.ToJsonString() ?? "error");
                break;
            case "step_finish" when part is not null && part["tokens"] is JsonObject tok:
                o.Turns++;
                o.Thinking += Json.Num(tok["reasoning"]);
                var cache = tok["cache"] as JsonObject;
                return new Usage(Json.Str(part, "id") ?? Guid.NewGuid().ToString(), Json.Str(part, "modelID") ?? "",
                    Json.Num(tok["input"]), Json.Num(tok["output"]), Json.Num(tok["reasoning"]),
                    Json.Num(cache?["read"]), Json.Num(cache?["write"]), 0);
        }
        return null;
    }

    public void Finish(Outcome o, int exitCode)
    {
        // opencode has no final result event: the run ends when the session goes idle.
        o.HasResult = o.Texts.Count > 0;
        if (exitCode != 0) o.IsError = true;
        o.Report = string.Join("\n", o.Texts).Trim();
        o.Subtype = o.IsError ? "error" : "success";
    }
}
