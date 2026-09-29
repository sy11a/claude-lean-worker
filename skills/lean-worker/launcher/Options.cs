using System.Globalization;

namespace LeanWorker;

internal sealed class Options
{
    public const string Usage = """
        LeanWorker: run one task in a minimal-context worker (Claude Code or opencode).

          --task <file>              task prompt (required unless --continue-from)
          --continue-from <run-dir>  fresh worker on a stopped run: its original task + its report as the handoff
                                     (replaces --task; profile defaults to that run's; model/runtime can be overridden)
          --profile <name>           profile from <runs-root>/profiles.json (default: its defaultProfile)
          --system <file>            per-task notes, appended after <runs-root>/project.md
          --name <name>              run name (default: the task file's folder name)
          --runtime <claude|opencode> worker runtime (default claude; profile key "runtime")
          --model <id>               override the profile's model (or model chain); provider/model, e.g.
                                     zai-coding-plan/glm-5.3; a bare id is an Anthropic model
          --effort <level>           low | medium | high | xhigh | max (claude runtime)
          --variant <name>           opencode model variant (profile key "variant")
          --tools <A,B,C>            built-in tools available to the worker (Claude Code names)
          --allow <pattern>          pre-approved tool pattern, repeatable, e.g. --allow "Bash(git diff:*)"
          --write-scope <glob>       a path the task may write, repeatable, relative to the git root (e.g. "src/Foo/**");
                                     files the run changed outside it are reported (profile key "writeScope")
          --mcp-config <file>        MCP servers for this run only (claude runtime; always --strict-mcp-config)
          --max-budget-usd <n>       spend cap for the run, metered by the launcher with prices.json
          --wrap-up-at <share>       past this share of the budget tools are blocked and the worker writes a
                                     handoff (default 0.8; 0 = off; profile key "wrapUpAt")
          --prices <file>            price file merged over the shipped and user ones (default <runs-root>/prices.json)
          --permission-mode <mode>   acceptEdits | dontAsk | plan | manual | auto | bypassPermissions
          --runs-root <dir>          default .lean-worker
          --claude-settings <file>   passed to claude as --settings (e.g. an apiKeyHelper for --bare)
          --timeout-minutes <n>      kill the worker after n minutes (default 60)
          --report-max-chars <n>     truncate the printed report (default 6000)
          --no-project-notes         do not give the worker <runs-root>/project.md
          --replace-system-prompt    replace Claude Code's system prompt instead of appending
          --mode <auto|bare|lean>    claude runtime. bare = claude --bare (API key; skips hooks, so no wrap-up);
                                     lean = the same minimal profile from flags (subscription login, key, or another
                                     provider). auto (default) = lean, or bare when a key is set and wrap-up is off
          --no-bare                  alias for --mode lean
          --keep-claude-md           do not exclude CLAUDE.md / AGENTS.md / .claude/rules (opencode: keep project config)
          --keep-memory              lean mode: keep auto memory (off by default)
          --cache-ttl <5m|1h|default> prompt-cache lifetime for the worker (default 5m; profile key "cacheTtl")
          --keep-hooks               lean mode: load the user's settings, hooks and plugins (off by default; profile
                                     key "keepHooks": true). Managed (organisation) hooks always run.
          --no-user-env              lean mode: do not carry the user settings' env block into the worker

        Other commands:
          hook --run-dir <dir>       the worker's pre-tool hook (installed by the launcher)
          quota [--provider <name>] [--json] [--max-age <seconds>]
                                     subscription quota of every provider with a quota adapter (e.g. zai-coding-plan)
          cost --claude <session-id|file.jsonl> [--provider <name>] | --opencode <session-id>
                                     price a manual session with prices.json
          stats [--since <yyyy-mm-dd>]  runs per profile and model: success rate, cost per success, quota used
          prices                     the merged price book: sources and each entry's date
        """;

    public string? TaskFile, Profile, SystemFile, Name, Model, Effort, Variant, McpConfig, PermissionMode, RunsRoot,
                   ClaudeSettings, CacheTtl, ContinueFrom, Runtime, PricesFile;
    public List<string>? Tools;
    public List<string> AllowedTools = [], WriteScope = [];
    public decimal? MaxBudgetUsd, WrapUpAt;
    public int TimeoutMinutes = 60, ReportMaxChars = 6000;
    public bool NoProjectNotes, ReplaceSystemPrompt, KeepClaudeMd, KeepMemory, KeepHooks, NoUserEnv, Help;
    public string Mode = "auto";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new LaunchException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--task": o.TaskFile = Next(); break;
                case "--continue-from": o.ContinueFrom = Next(); break;
                case "--profile": o.Profile = Next(); break;
                case "--system": o.SystemFile = Next(); break;
                case "--name": o.Name = Next(); break;
                case "--runtime": o.Runtime = Next(); break;
                case "--model": o.Model = Next(); break;
                case "--effort": o.Effort = Next(); break;
                case "--variant": o.Variant = Next(); break;
                case "--tools": o.Tools = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--allow": o.AllowedTools.Add(Next()); break;
                case "--write-scope": o.WriteScope.Add(Next()); break;
                case "--mcp-config": o.McpConfig = Next(); break;
                case "--max-budget-usd": o.MaxBudgetUsd = decimal.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--wrap-up-at": o.WrapUpAt = decimal.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--prices": o.PricesFile = Next(); break;
                case "--permission-mode": o.PermissionMode = Next(); break;
                case "--runs-root": o.RunsRoot = Next(); break;
                case "--claude-settings": o.ClaudeSettings = Next(); break;
                case "--timeout-minutes": o.TimeoutMinutes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--report-max-chars": o.ReportMaxChars = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-project-notes": o.NoProjectNotes = true; break;
                case "--replace-system-prompt": o.ReplaceSystemPrompt = true; break;
                case "--no-bare": o.Mode = "lean"; break;
                case "--mode": o.Mode = Next(); break;
                case "--keep-claude-md": o.KeepClaudeMd = true; break;
                case "--keep-memory": o.KeepMemory = true; break;
                case "--keep-hooks": o.KeepHooks = true; break;
                case "--no-user-env": o.NoUserEnv = true; break;
                case "--cache-ttl": o.CacheTtl = Next(); break;
                case "--no-hooks": break; // hooks are off by default; accepted for compatibility
                case "-h" or "--help": o.Help = true; break;
                default: throw new LaunchException($"unknown option '{args[i]}' (see --help)");
            }
        }
        return o;
    }
}
