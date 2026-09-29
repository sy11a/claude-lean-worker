# claude-lean-worker

A Claude Code skill that runs one coding task in a separate, minimal-context worker
(`claude -p --bare`). The orchestrating session gets back the worker's report together
with its token usage and cost.

> **Quick install through your agent:** in a Claude Code session in your project, say
> *"Install the lean-worker skill for this project, following
> https://github.com/sy11a/claude-lean-worker/blob/main/INSTALL.md"*.
> [INSTALL.md](INSTALL.md) walks the agent through the whole setup and tells it when to stop and ask you.
> A fuller prompt to paste is in [AGENT-INSTALL-PROMPT.md](AGENT-INSTALL-PROMPT.md).

## Why

An in-session subagent inherits the project's CLAUDE.md, the skill listing, every MCP
server and every tool schema. That is often 40-60k tokens before the subagent does anything,
and every API call it makes re-reads all of it. A `--bare` worker loads only what you pass
it: the project notes, the task, and the tools the task needs.

Every run is recorded, so you can see what each delegated task actually cost:

```
LEAN-WORKER RESULT
run:      .lean-worker/runs/20260929-133150-add-greeting
status:   success  (subtype=success, reason=completed, exit=0)
model:    claude-haiku-4-5, effort low, profile code, mode lean
work:     3 turns, 3 API calls, 0m13s
cost:     $0.0105 (list price reported by Claude Code)
tokens:   input 25 | cache write 971 | cache read 43,573 | output 830 (thinking 501)
context:  first call 14,283 | peak 15,253
--- worker report ---
...
```

This is real output from a test run with `--no-bare`. A `--bare` run starts smaller.

## Requirements

- Claude Code, logged in with either:
  - an **API key** (`ANTHROPIC_API_KEY` or an `apiKeyHelper`). Workers then run in **bare** mode
    (`claude --bare`), or
  - a **subscription login** (Pro, Max, Team or **Enterprise**; `claude` + `/login`). Workers then run in
    **lean** mode. `--bare` cannot use a subscription login, so the launcher builds the same
    minimal profile from flags instead. See [Authentication and modes](#authentication-and-modes).
- .NET SDK 8 or newer. The launcher targets `net8.0` with `RollForward=LatestMajor`, so it
  also runs on newer runtimes. It has no NuGet dependencies.

## Setup after download

### 1. Run the installer (no agent involved)

From the downloaded repository, in PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -ProjectPath C:\src\my-product -SmokeTest
```

The installer:

1. **Checks prerequisites:** `claude` with `--bare`, a .NET SDK 8 or newer, and whether
   `ANTHROPIC_API_KEY` is set. A missing key is a warning, not a failure.
2. **Installs the skill** into `%USERPROFILE%\.claude\skills\lean-worker`. Use
   `-Scope Project` to install into `<project>\.claude\skills` instead.
3. **Builds the launcher once**, so the first worker starts immediately.
4. **Prepares the project** when `-ProjectPath` is given:
   - creates `.lean-worker\project.md` and `.lean-worker\profiles.json` from the templates.
     Files that already exist are never overwritten.
   - adds `.lean-worker/runs/`, `.lean-worker/inbox/` and `.lean-worker/runs.jsonl` to `.gitignore`.
   - adds the allow rule `Bash(dotnet run --project:*)` to `<project>\.claude\settings.json`,
     so the orchestrator can start workers without a permission prompt. The file is backed up
     first (`settings.json.bak-<stamp>`) and is re-serialised, so any formatting is not kept. Pass
     `-SkipPermission` to leave it alone and add the rule yourself.
5. **Runs one tiny worker** when `-SmokeTest` is given. It uses Haiku, is read-only and has a
   budget of $0.10. The installer prints that worker's result block.

The installer is safe to re-run: it updates the skill and leaves your project files alone.

### 2. Fill in the project notes and profiles

Restart Claude Code, or start a new session, so it picks up the skill. Then, in a session in
your project:

```
/lean-worker set up .lean-worker/project.md and profiles.json for this repository
```

The orchestrator reads the repository and asks you about anything it cannot see. It fills in:

- **`.lean-worker/project.md`**: the notes every worker receives. Stack, layout, build, test and
  lint commands, conventions, prohibitions, and where to look. Keep it short (target under 2k
  tokens), because it is paid for on every worker API call.
- **`.lean-worker/profiles.json`**: named profiles (`read`, `edit`, `code`, `research`, `review`).
  Each one sets the model, effort, tools, pre-approved commands and budget. The `{build command}`
  and `{test command}` placeholders are replaced with your own commands, and you add profiles
  for your own task classes.

Then **read and edit both files by hand**. They are yours, and the skill never overwrites your edits.

### 3. Authentication and modes

The launcher picks the mode itself (`--mode auto`, the default). The mode is shown in every result block.

| Your Claude Code login | Mode | What the worker runs |
|---|---|---|
| `ANTHROPIC_API_KEY` set, or `--claude-settings <file>` with an `apiKeyHelper` | **bare** | `claude -p --bare`: loads no CLAUDE.md, hooks, plugins, skills, auto-memory or MCP |
| Subscription login: Pro, Max, Team, **Enterprise** | **lean** | `claude -p` with CLAUDE.md, CLAUDE.local.md, AGENTS.md and `.claude/rules` excluded (`claudeMdExcludes`), auto memory off, your hooks off (organisation-managed hooks still run), skills disabled, and no MCP |

Nothing needs configuring for an Enterprise subscription. Stay logged in to `claude` as usual;
the worker uses the same login.

- **Don't set an API key "just for the workers" on an Enterprise seat** unless your organisation
  gives you one. `--bare` cannot use the subscription login; the lean mode exists for exactly that.
- **Cost on a subscription.** The `cost:` line is the list-price equivalent that Claude Code reports.
  On Enterprise you are not billed that amount: the tokens count against your plan's usage limits.
  It is still the right number for comparing tasks and profiles.
- **Size.** A trivial task, measured with a subscription login, had a first call of about 5k
  tokens in lean mode with its defaults (hooks off). A default in-session subagent is typically
  40-60k. Bare mode is expected to be about as small.
- **User and managed settings still apply in lean mode.** That includes enterprise policy,
  proxies and your user hooks. Bare mode skips hooks.

**What bare turns off, and how lean covers each item.** Measured with a subscription login,
one-call probe:

| Item | bare | lean default | Control in lean | Measured effect |
|---|---|---|---|---|
| CLAUDE.md, CLAUDE.local.md, AGENTS.md, `.claude/rules` | off | off | `--keep-claude-md` to keep them | about −4k (a 13.5 KB CLAUDE.md) |
| Auto memory (also its instructions in the system prompt) | off | off | `--keep-memory` to keep it | about −3k, even with no memory files |
| Hooks (user, project, plugins) | off | off (`disableAllHooks`). **Managed hooks still run:** a non-managed `disableAllHooks` cannot turn them off | `--keep-hooks`, or `"keepHooks": true` in a profile | about −1k on the test machine; depends on your hooks |
| Skills | off | off | always off (`--disable-slash-commands`) | small |
| MCP servers | off | off | always `--strict-mcp-config`; a profile can name servers | depends on the servers |
| Plugins, plugin sync, LSP | off | loaded | with skills, hooks and MCP off, and the worker's tool list fixed, a plugin has no remaining way into the worker's context | none measured |
| Background traffic (updates, telemetry) | off | on | not changed by the launcher. `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` in your environment turns it off; check your organisation's telemetry policy first | no context effect |
| Keychain | not read | read | this is how the subscription login works | n/a |

With the defaults, a lean worker's first call was about 5.2k tokens.

**Your integrations do not leak into the worker; your organisation's controls still apply.**
Lean mode shuts every channel through which personal customisations enter a session:

- CLAUDE.md files at every level, including `~/.claude/CLAUDE.md`, plus AGENTS.md and rules;
- auto memory;
- your user, project and plugin hooks;
- your output style (the worker's settings force `outputStyle: "default"`);
- skills, plugin commands and agents (not reachable with skills disabled and a fixed tool list);
- MCP servers.

The hook check was run on this machine: with hooks on, a plugin's SessionStart text reached the
worker; with the default, nothing did. Hooks deployed by your organisation through managed
settings keep running, because the docs state that a `disableAllHooks` set outside managed
settings cannot disable them.

Two things from your user settings do still reach the worker, by design:

- the `env` block (for example proxies);
- `permissions.allow` rules. A broad rule such as `Bash(*)` in your user settings also
  pre-approves those commands for workers. Keep broad rules out of user settings if that matters.

`CLAUDE_CODE_SAFE_MODE=1` also works with a subscription login (5.2k in the same probe). It turns
off CLAUDE.md, skills, plugins, hooks, MCP, agents, LSP and auto memory in one go. The launcher
does not use it, because it is documented as a troubleshooting mode, and the explicit settings
above can be switched one at a time and are recorded in each run's `settings.json`.

## How it fits your Claude Code flow

The orchestrator starts the launcher with its normal Bash tool, in the background. When the
worker finishes, Claude Code notifies the orchestrator, and the notification carries the
launcher's output. The tokens and cost therefore land in the orchestrating session by
themselves: nothing polls, and nobody has to open a separate log.

```
You:           /lean-worker Add retry with backoff to OrderClient.Send; done when the OrderClient tests pass
Orchestrator:  writes .lean-worker/inbox/order-retry/task.md
               Bash (background): dotnet run --project <skill-dir>/launcher -c Release -- --task ... --profile code
               ... the worker runs; the orchestrator waits ...
               <- completion notification with the launcher output:
                  LEAN-WORKER RESULT
                  status: success ...
                  cost:   $0.3812
                  tokens: input 210 | cache write 18,300 | cache read 351,000 | output 7,900
                  context: first call 5,100 | peak 31,000
                  --- worker report --- ...
               Bash: <your test command>   <- checks the done-criterion itself
Orchestrator -> you: "Done, tests green. Worker: 14 API calls, $0.38."
```

The orchestrator is told to read only the result block, and not the worker's transcript or the
full diff. That keeps its own context, which it re-reads on every turn, small. Every run is also
recorded as one line in `.lean-worker/runs.jsonl` (see below).

**Later, optionally: an MCP tool.** The launcher can be wrapped in a .NET MCP server built with the
official `ModelContextProtocol` C# SDK. It would expose `run_worker(task, profile)`,
`worker_status(runId)` and `worker_costs(since)`.

- **Gains:**
  - structured JSON results instead of a text block;
  - one tool to grant permission to;
  - a spend summary in one call.
- **Costs:**
  - an MCP call blocks the orchestrator and has a timeout, so long tasks would need a start/status pair;
  - one more server to register;
  - its tool schemas enter the context.

This is not built. Use the Bash flow first and build the MCP server when a real need shows up.

## Launcher reference

Run from the project root (`.lean-worker/` is resolved relative to the current directory):

```
dotnet run --project <skill-dir>/launcher -c Release -- --task .lean-worker/inbox/fix-parser/task.md --profile code
```

| Option | Default | Meaning |
|---|---|---|
| `--task <file>` | required | Task prompt, piped to the worker |
| `--profile <name>` | `defaultProfile` in profiles.json | Named profile |
| `--system <file>` | none | Per-task notes, appended after `project.md` |
| `--model`, `--effort`, `--tools A,B`, `--allow <pattern>` (repeatable), `--mcp-config`, `--max-budget-usd`, `--permission-mode` | from profile | Per-run overrides |
| `--timeout-minutes <n>` | `60` | Kill the worker after n minutes |
| `--no-project-notes` | off | Do not send `project.md` |
| `--replace-system-prompt` | off | Replace Claude Code's system prompt instead of appending to it |
| `--mode auto\|bare\|lean` | `auto` | `bare` if an API key or `--claude-settings` is present, otherwise `lean` (subscription) |
| `--keep-claude-md` | off | Lean mode: do not exclude CLAUDE.md / AGENTS.md / `.claude/rules` |
| `--keep-memory` | off | Lean mode: keep auto memory |
| `--cache-ttl 5m\|1h\|default` | `5m` | Prompt-cache lifetime for the worker (profile key `cacheTtl`). See below |
| `--keep-hooks` | off | Lean mode: keep your user/project/plugin hooks (or `"keepHooks": true` in a profile). Managed hooks always run |
| `--claude-settings <file>` | none | Passed to `claude` as `--settings`, e.g. a file with an `apiKeyHelper`. In lean mode it is merged with the exclusions |
| `--no-bare` | off | Alias for `--mode lean` |

A setting comes from the command-line option if one is given, otherwise from the profile,
otherwise from the built-in default. The worker always runs with `--strict-mcp-config`, so it
gets no MCP server unless the profile or `--mcp-config` names one.

Exit codes: `0` success, `1` the worker reported an error, `2` the launcher failed.

## Cost notes from real runs

- **Cache TTL.** A `claude -p` worker on a subscription login writes its cache with the 1-hour TTL by
  default, which costs 2x base input. An API key defaults to 5 minutes, at 1.25x. A worker's calls
  follow each other within seconds, so the launcher sets `CLAUDE_CODE_PROMPT_CACHE_TTL=5m` for the
  worker. On two measured runs the 1-hour writes were 25-45% of the cost; at 5m they cost about 40% less.
- **Tools are the base now.** With instructions excluded, most of a worker's first call is tool schemas.
  Bash, Edit, Write and WebFetch are large. Give each profile only the tools its task class uses.
- **Pre-approve harmless read commands** (`ls`, `cat`, `pwd`) in coding profiles. Each denied call is a
  wasted API call.
- **Research workers** grow with every fetched page: a docs task peaked at 99.5k tokens, and the
  WebFetch summaries cost another 16%. Give exact URLs and split research by source.
- **Keep the orchestrator short-lived.** Its long history is re-read on every one of its turns, so it is
  easily the most expensive part. Let a `review` worker check a worker's result, and read only verdicts.

## What is recorded

`.lean-worker/runs/<stamp>-<name>/` holds:

- `task.md`
- `system.md`, the notes the worker actually received
- `command.txt`
- `stream.jsonl`, the raw `stream-json` output
- `summary.json`
- `report.md`

`.lean-worker/runs.jsonl` gets one JSON line per run, recording:

- profile, model and effort;
- status, turns, and API calls (deduplicated by message id);
- cost;
- the input / cache-write / cache-read / output / thinking token split;
- first-call and peak context.

## Verification status

Checked on 2026-09-29 with Claude Code 2.1.284 and .NET SDK 10.

**Tested on Linux:**
- the launcher builds with no warnings;
- real worker runs with `--no-bare` (subscription auth), end to end, driven by a profile and project notes;
- the refusal when no API key is set in `--bare` mode;
- the refusal when an unknown profile is named;
- `install.ps1` under PowerShell 7.6: a full install into a sandbox home and project, the smoke
  test, and a re-run that duplicates nothing.

- lean mode with a subscription login, checked with codeword probes (these figures are from before auto memory was turned off by default): the worker does not see the
  project's CLAUDE.md, AGENTS.md or `.claude/rules` (9.1k first call), and does see them with
  `--keep-claude-md` (13.4k). It also covers the AGENTS.md fallback, which loads when CLAUDE.md is
  excluded alone.

**Not yet tested:**
- a `--bare` run with an API key;
- Windows, including `claude` installed as an npm `.cmd` shim, which the launcher starts through `cmd.exe`;
- `install.ps1` under Windows PowerShell 5.1. It is written ASCII-only and without 7-only syntax.

## License

MIT
