# claude-lean-worker

A Claude Code skill that runs one coding task in a separate, minimal-context worker
(`claude -p --bare`). The orchestrating session gets back the worker's report together
with its token usage and cost.

> **Quick install through your agent:** in a Claude Code session in your project, say
> *"Install the lean-worker skill for this project, following
> https://github.com/sy11a/claude-lean-worker/blob/main/INSTALL.md"*.
> [INSTALL.md](INSTALL.md) walks the agent through the whole setup and tells it when to stop and ask you.

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
model:    claude-haiku-4-5, effort low, profile code, bare=False
work:     3 turns, 3 API calls, 0m13s
cost:     $0.0105 (list price reported by Claude Code)
tokens:   input 25 | cache write 971 | cache read 43,573 | output 830 (thinking 501)
context:  first call 14,283 | peak 15,253
--- worker report ---
...
```

This is real output from a test run with `--no-bare`. A `--bare` run starts smaller.

## Requirements

- Claude Code with `--bare` in `claude --help`, authenticated by **API key**. `--bare` reads
  only `ANTHROPIC_API_KEY` (or an `apiKeyHelper` passed via `--settings`); it never reads
  OAuth or the keychain.
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

### 3. API key

`--bare` reads only `ANTHROPIC_API_KEY`, or an `apiKeyHelper` in a settings file passed with
`--claude-settings <file>`. It never uses OAuth or the keychain. Make sure the key is in the
environment Claude Code runs in. For example, set it once for your user with
`setx ANTHROPIC_API_KEY <key>` and open a new terminal.

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
| `--claude-settings <file>` | none | Passed to `claude` as `--settings`, e.g. a file with an `apiKeyHelper` |
| `--no-bare` | off | Same lean profile without `--bare`, e.g. when there is no API key |

A setting comes from the command-line option if one is given, otherwise from the profile,
otherwise from the built-in default. The worker always runs with `--strict-mcp-config`, so it
gets no MCP server unless the profile or `--mcp-config` names one.

Exit codes: `0` success, `1` the worker reported an error, `2` the launcher failed.

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

**Not yet tested:**
- a `--bare` run with an API key;
- Windows, including `claude` installed as an npm `.cmd` shim, which the launcher starts through `cmd.exe`;
- `install.ps1` under Windows PowerShell 5.1. It is written ASCII-only and without 7-only syntax.

## License

MIT
