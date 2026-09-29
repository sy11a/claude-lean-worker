---
name: lean-worker
description: Delegate one well-scoped coding task from an orchestrating session to a separate minimal-context worker process (Claude Code `claude -p`, or opencode `opencode run`, on Claude or other providers such as GLM, DeepSeek, Qwen or MiniMax) and get back its report plus exact token usage and cost; near its budget the worker hands off instead of stopping blind, and a fresh worker can continue. Use when a task has a clear goal and a done-criterion you can check with a command (implement a change, write tests, a mechanical refactor, a focused investigation), and you want it done without loading this session's full project context, skills and MCP servers into the worker. Also use to set up or update the project's worker notes and profiles (.lean-worker/project.md, .lean-worker/profiles.json).
---

# Lean worker

You are the orchestrator. For a well-scoped task you do not do the work in this session and
you do not use an in-session subagent. A subagent inherits the project's CLAUDE.md, the skill
listing, every MCP server and every tool schema, which is often 40-60k tokens before it starts
and is re-read on every call. A lean worker loads nothing on its own. It gets only the
project notes, the task and the tools you give it.

The launcher is a small .NET console app in `launcher/` in this skill's base directory, which is
shown when the skill loads. Below, `<skill-dir>` means that absolute path.

The launcher meters the worker's spend live with the price book (`prices.json`). Past the wrap-up
share of the budget (default 80%) every tool call is blocked, so the worker's last message is a
`HANDOFF` block; at the budget the launcher stops the worker.

## Prerequisites (check once per session)

- `claude` is logged in. The launcher picks the mode itself: **lean** (the minimal profile built
  from flags: no CLAUDE.md, AGENTS.md, rules, user settings, hooks, plugins, skills or MCP) on a
  subscription login or a key, and **bare** (`claude --bare`) only with an API key and wrap-up off.
  Do not ask the user for an API key when they are on a subscription.
- For profiles with `"runtime": "opencode"`: `opencode` is on PATH and logged in to the profile's
  provider. For another provider's model in the claude runtime: its key is in the provider's
  `keyEnv` variable (see `prices.json`) or in opencode's login.
- `claude` is on PATH, and `claude --help` lists `--bare`.
- A .NET SDK 8 or newer (`dotnet --list-sdks`). The first run builds the launcher, which takes a
  few seconds; later runs reuse the build.

## 0. Project setup (once per project, then maintained)

The skill knows nothing about the project's stack. Two files in the project configure it.
If the user ran `install.ps1 -ProjectPath`, both already exist as templates. Fill them in;
do not recreate them.
Both are the user's files: propose content, let the user edit it, and never overwrite
their edits.

1. **`.lean-worker/project.md`** holds the notes every worker gets: stack, repository layout,
   build/test/lint commands, conventions that matter for changes, prohibitions, and where to
   look. Start from `<skill-dir>/templates/project.md`. Fill it by reading the repository
   (build files, README, CLAUDE.md, CI config) and asking the user about anything you cannot
   see. Delete the quoted template block at its top (workers would otherwise pay for it on every
   call) and replace every `{...}` placeholder. Keep it under about 2k tokens and link to long documents instead of pasting them,
   because every line is paid on every worker call.
2. **`.lean-worker/profiles.json`** holds the named worker profiles (runtime, model or model chain,
   effort, tools, pre-approved commands, budget, `wrapUpAt`). Start from `<skill-dir>/templates/profiles.json`. Replace
   the `{build command}` and `{test command}` placeholders with the project's real commands,
   and add a profile for any task class the project needs, such as a slow integration-test suite
   or a code generator. The base template runs on Claude. When the user has another model plan, ask
   which, and merge the matching family from `<skill-dir>/templates/profiles/`. Each family has
   `read-<family>`, `edit-<family>`, `code-<family>` and `review-<family>`, and its `_comment` holds
   the plan's notes. The families:
   - `minimax.json`: MiniMax Token Plan;
   - `glm.json`: z.ai GLM Coding Plan;
   - `alibaba.json`: Alibaba Model Studio Token Plan (Qwen, DeepSeek). Not verified: it needs your opencode provider id and prices.

   Each family's chains end in Claude, which runs only on an `escalate:` go.

`.lean-worker/` is shared by every coding tool that uses the launcher (Claude Code and opencode
orchestrators alike); nothing tool-specific goes in it. An optional `.lean-worker/prices.json` adds or
overrides prices and providers (see the README); never edit the shipped `<skill-dir>/launcher/prices.json`.

Also make sure `.lean-worker/runs/` and `.lean-worker/inbox/` are in `.gitignore`. Whether
`project.md` and `profiles.json` are committed is the user's decision; recommend committing them.

When a worker fails because the notes are missing something (a command, a convention, a
path), propose a one-line addition to `project.md`. Do not paste the explanation into every task.

## 1. Write the task

Create `.lean-worker/inbox/<task-name>/task.md` from `<skill-dir>/templates/task.md`:

- **Goal:** one paragraph.
- **Start here:** the paths to read first, which saves the worker exploring.
- **Done when:** a command whose output proves it.
- **Boundaries:** what must not be touched.
- **Report format:** at most 30 lines.

A per-task `system.md` is optional (see `templates/system.md`). Use it only for notes that
matter to this one task.

## 2. Pick the profile

Choose a profile from `.lean-worker/profiles.json` by task class. The template ships with
`read`, `edit`, `code`, `research` and `review`. If none fits, add one to the file rather than
passing a long list of flags. For a one-off, any profile value can be overridden per run with
`--model`, `--effort`, `--tools`, `--allow` (repeatable), `--mcp-config`, `--max-budget-usd` or
`--permission-mode` (`--help` lists everything).
Design work and hard judgement stay with you; they do not go to a worker.

In lean mode the worker gets none of the user's personal integrations: CLAUDE.md at every level,
auto memory, user/project settings, user/project/plugin hooks, output style, skills and MCP are all
excluded (only the user settings' `env` block is carried over). Organisation-managed hooks still run.
Use `--keep-hooks` or `"keepHooks": true` in a profile only when a task needs one of the user's own hooks.
An opencode worker runs on a clean opencode config: no global instructions, plugins, skills or MCP.
Its Bash runs only the `allowedTools` patterns; any other command is denied and the worker gets a tool
error. opencode checks every segment of a pipeline or chain (`jq … | head`), so each segment must match.

A profile's `model` may be a **chain**, e.g. `["zai-coding-plan/glm-5.3", "deepseek/deepseek-v4-flash",
"claude-sonnet-5-5"]`. The launcher takes the first model with headroom: a subscription model while
its quota windows are under the thresholds in `prices.json`, a metered model always. The result block
says which model ran and why; do not pick the model yourself unless the user asks.

## 3. Run it

Run it with the shell tool **in the background**, because a worker can outlast the tool's
timeout. Wait for the completion notification; do not poll.

```
dotnet run --project "<skill-dir>/launcher" -c Release -- --task ".lean-worker/inbox/<task-name>/task.md" --profile code
```

Run it from the project root, because `.lean-worker/` is resolved relative to the current directory.

## 4. Read the result

The script prints one block:

```
LEAN-WORKER RESULT
run:      .lean-worker/runs/<stamp>-<task-name>
status:   success  (subtype=success, reason=completed, exit=0)
model:    zai-coding-plan/glm-5.3 (first in chain), effort medium, profile code
runtime:  claude, mode lean, hooks off (managed hooks still run), cache 5m
work:     12 turns, 12 API calls, 3m41s
cost:     $0.1123 (list-price equivalent; subscription, not billed)
budget:   $3, wrap-up at $2.4, 12 hook checks
tokens:   input 310 | cache write 0 | cache read 402,118 | output 9,870 (thinking 3,200)
context:  first call 4,812 | peak 38,420
quota:    5h 10% -> 12%, weekly 41% -> 42%
--- worker report ---
...
```

- Read only this block. Do not read `stream.jsonl` or the whole diff into this session; that
  re-grows the context you are keeping small.
- Verify the done-criterion yourself with one command. The worker saying it is done is not verification.
- Tell the user the status, the model and the cost line in one or two sentences. On a subscription,
  also give the `quota:` line: it is how the user sees what is left on the plan.
- For a closer look, run `git diff --stat`, then read only the files that matter.

## 5. When it wraps up or fails

- `status: wrapped-up` (exit 3): the worker reached the wrap-up share of its budget and left a
  `HANDOFF` (Done / Remaining / Files touched / State / Next step). Tell the user the spend so far and
  the Remaining list, and **continue only on the user's go**. Then run the launcher with the
  `continue:` line's arguments (`--continue-from "<run-dir>"`, no `--task`): a fresh worker gets the
  original task plus the handoff, with the same profile and the full budget again. Never resume the
  old session; it would re-read the whole context.
- An `escalate:` line means the run failed on a model that has a next one in the profile's chain.
  Offer it to the user in one sentence; run it only on their go.
- `status: budget-exceeded`: the launcher stopped the worker at the budget before it could hand off.
  Check the state with `git status`, then lower `wrapUpAt` for that profile or raise its budget.
- Otherwise the worker's session is not kept. Sharpen `task.md` (goal, start-here paths, scope) or
  split the task, and run again. Each run gets its own directory.
- If the cause is general (a missing command, an unknown convention), fix `project.md` or the
  profile instead of the task.
- `status: error` with `reason: api_error` and "Not logged in" means the worker could not
  authenticate. In bare mode the key is missing or invalid. In lean mode `claude` is not logged
  in: ask the user to run `claude` and `/login`.
- On a subscription, the `cost:` line is the list-price equivalent, not a bill; the tokens count
  against the plan's usage limits. Report it as such.
- `note: no price for <model>` means the price book lacks the model; propose a line for
  `.lean-worker/prices.json`.
- If the summary reports permission denials, add the needed commands to the profile's `allowedTools`.
- Give a task that edits code its write scope: `--write-scope "src/Foo/**"` (repeatable, relative to the git
  root; a directory covers everything under it) or the profile key `writeScope`. The launcher compares the
  working tree before and after the run: the `files:` line counts what changed, `summary.json` lists it
  (`changed_files`, `out_of_scope`), and a `WARNING` names the files outside the scope. The status does not
  change: check those files (revert or accept them) before you accept the run. A continuation keeps the scope.

## Review run (optional)

Run the `review` profile with the task "try to refute that the change meets <done-criterion>;
list findings with file:line".

## Quota, costs and statistics

When the user asks what is left on a subscription, or how much something cost, run the launcher's
other commands (same `dotnet run --project "<skill-dir>/launcher" -c Release --` prefix):

- `quota`: the usage windows of every subscription with a quota adapter (z.ai GLM Coding Plan today).
- `cost --claude <session-id>` or `cost --opencode <session-id>`: a manual session priced with the book.
- `stats [--since <date>]`: per profile and model, success rate, cost per success and quota used.
- `prices`: the merged price book, with each entry's date.

## Records

Every run leaves `.lean-worker/runs/<stamp>-<name>/` containing `task.md`, `system.md` (the
notes the worker actually received), `command.txt`, `stream.jsonl`, `summary.json` and
`report.md` (plus `wrapup.json` and `hook.log` when wrap-up is on). It also appends one JSON line
to `.lean-worker/runs.jsonl` with the profile, runtime, provider, model, billing, status, turns,
API calls, cost, budget, wrap-up, token split, first-call and peak context, and quota used.
