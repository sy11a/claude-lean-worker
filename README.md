# claude-lean-worker

A Claude Code skill that runs one coding task in a separate, minimal-context worker
(`claude -p --bare`). The orchestrating session gets back the worker's report together
with its token usage and cost.

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

## Install

Copy the skill folder into your user skills, or into one project's skills:

```powershell
# user-wide
Copy-Item -Recurse .\skills\lean-worker "$env:USERPROFILE\.claude\skills\lean-worker"
# or per project
Copy-Item -Recurse .\skills\lean-worker .\.claude\skills\lean-worker
```

## Configure it for your project (stack-neutral)

The skill knows nothing about your stack. Two files in your project configure it. The
orchestrating session drafts them from `templates/`, and you edit them by hand:

- **`.lean-worker/project.md`**: notes every worker receives. Stack, layout, build, test and lint
  commands, conventions, prohibitions, and where to look. Keep it short (target under 2k tokens),
  because it is paid on every worker API call.
- **`.lean-worker/profiles.json`**: named profiles (`read`, `edit`, `code`, `research`, `review`
  in the template). Each one sets the model, effort, tools, pre-approved commands and budget.
  Replace the `<build command>` and `<test command>` placeholders with your own, and add
  profiles for your task classes.

Add `.lean-worker/runs/` and `.lean-worker/inbox/` to `.gitignore`. Run directories contain the
full worker transcript.

## Use

In the orchestrating session, ask for it directly ("use lean-worker to implement …"), or
let Claude pick it for a well-scoped task. The skill tells the orchestrator to:

1. Write `.lean-worker/inbox/<name>/task.md`: the goal, the paths to start from, a
   done-criterion that can be checked with a command, the boundaries, and the report format.
2. Run the launcher in the background with a profile.
3. Read only the printed result block, then verify the done-criterion itself.

By hand, from the project root:

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
- the refusal when an unknown profile is named.

**Not yet tested:**
- a `--bare` run with an API key;
- Windows, including `claude` installed as an npm `.cmd` shim, which the launcher starts through `cmd.exe`.

## License

MIT
