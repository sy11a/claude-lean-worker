# claude-lean-worker

A Claude Code skill that runs one coding task in a separate, minimal-context worker
(`claude -p --bare`). The orchestrating session gets back the worker's report together
with its token usage and cost.

## Why

An in-session subagent inherits the project's CLAUDE.md, the skill listing, every MCP
server and every tool schema. That is often 40-60k tokens before the subagent does anything,
and all of it is re-read on every API call the subagent makes. A `--bare` worker loads only
what you pass it explicitly: a short system note, the task, and the tools the task needs.

Every run is recorded, so you can see what each delegated task actually cost:

```
LEAN-WORKER RESULT
status:   success  (subtype=success, reason=completed, exit=0)
work:     3 turns, 3 API calls, 0m15s
cost:     $0.0226 (list price reported by Claude Code)
tokens:   input 25 | cache write 7,113 | cache read 37,625 | output 923 (thinking 607)
context:  first call 14,277 | peak 15,358
--- worker report ---
...
```

(This is the real output of the test run below, which used `-NoBare`. A `--bare` run
starts smaller.)

## Requirements

- Claude Code with `--bare` in `claude --help`, authenticated by **API key**. `--bare`
  reads only `ANTHROPIC_API_KEY` (or an `apiKeyHelper` passed via `--settings`) and never
  OAuth or the keychain.
- Windows PowerShell 5.1 or PowerShell 7+. On macOS and Linux, PowerShell 7 (`pwsh`).

## Install

Copy the skill folder into your user skills, or into one project's skills:

```powershell
# user-wide
Copy-Item -Recurse .\skills\lean-worker "$env:USERPROFILE\.claude\skills\lean-worker"
# or per project
Copy-Item -Recurse .\skills\lean-worker .\.claude\skills\lean-worker
```

Add `.lean-worker/` to the project's `.gitignore`. Run directories contain the full
worker transcript.

## Use

In the orchestrating session, ask for it directly ("use lean-worker to implement …"), or
let Claude pick it for a well-scoped task. The skill tells the orchestrator how to:

1. Write `.lean-worker/inbox/<name>/task.md` (goal, start-here paths, a done-criterion you
   can check with a command, boundaries, report format) and optionally a short `system.md`.
   Templates are in `skills/lean-worker/templates/`.
2. Run `scripts/Invoke-LeanWorker.ps1` in the background with a model, effort, tool and
   permission profile that fits the task class.
3. Read only the printed result block, then verify the done-criterion itself.

You can run the launcher by hand too:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\lean-worker\scripts\Invoke-LeanWorker.ps1 `
  -TaskFile .lean-worker\inbox\fix-parser\task.md `
  -SystemFile .lean-worker\inbox\fix-parser\system.md `
  -Model claude-sonnet-5 -Effort medium `
  -AllowedTools 'Bash(dotnet build:*)','Bash(dotnet test:*)'
```

| Parameter | Default | Meaning |
|---|---|---|
| `-TaskFile` | required | Task prompt, piped to the worker |
| `-SystemFile` | none | Appended to Claude Code's system prompt (`-ReplaceSystemPrompt` to replace it) |
| `-Model` | `claude-sonnet-5` | Worker model |
| `-Effort` | `medium` | `low` … `max` |
| `-Tools` | `Read,Edit,Write,Glob,Grep,Bash` | Built-in tools available to the worker |
| `-AllowedTools` | none | Pre-approved tool patterns, e.g. `'Bash(dotnet test:*)'` |
| `-McpConfig` | none | MCP servers for this run only (always `--strict-mcp-config`) |
| `-MaxBudgetUsd` | `2` | Spend cap for the run |
| `-PermissionMode` | `acceptEdits` | Claude Code permission mode |
| `-RunsRoot` | `.lean-worker` | Where runs and `runs.jsonl` go |
| `-NoBare` | off | Same lean profile without `--bare` (e.g. no API key) |

Exit codes: `0` success, `1` the worker reported an error, `2` the launcher failed.

## What is recorded

`.lean-worker/runs/<stamp>-<name>/` holds:

- `task.md`, `system.md`
- `command.txt`
- `stream.jsonl` (raw `stream-json` output)
- `summary.json`
- `report.md`

`.lean-worker/runs.jsonl` gets one JSON line per run. It records model, effort, status,
turns, API calls (deduplicated by message id), cost, the input / cache-write / cache-read /
output / thinking token split, first-call context and peak context. This is enough to
compare the cost of task classes and profiles over time.

## Verification status

What has been checked, and where. The date is 2026-09-29, with Claude Code 2.1.284.

- **Tested:** PowerShell 7.6 on Linux:
  - a real worker run with `-NoBare` (subscription auth), end to end;
  - the missing-API-key refusal in `--bare` mode.
- **Not yet tested:**
  - a `--bare` run with an API key;
  - Windows PowerShell 5.1;
  - Windows itself.

The script avoids PowerShell 7-only syntax and non-ASCII characters so that it can run on 5.1.
Please open an issue if something breaks there.

## License

MIT
