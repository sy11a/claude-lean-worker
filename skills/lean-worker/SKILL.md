---
name: lean-worker
description: Delegate one well-scoped coding task from an orchestrating Claude Code session to a separate minimal-context worker process (`claude -p --bare`) and get back its report plus exact token usage and cost. Use when a task has a clear goal and a done-criterion you can check with a command (implement a change, write tests, a mechanical refactor, a focused investigation), and you want it done without loading this session's full project context, skills and MCP servers into the worker. Also use to set up or update the project's worker notes and profiles (.lean-worker/project.md, .lean-worker/profiles.json).
---

# Lean worker

You are the orchestrator. For a well-scoped task you do not do the work in this session and
you do not use an in-session subagent. A subagent inherits the project's CLAUDE.md, the skill
listing, every MCP server and every tool schema, which is often 40-60k tokens before it starts
and is re-read on every call. A `--bare` worker loads nothing on its own. It gets only the
project notes, the task and the tools you give it.

The launcher is a small .NET console app in `launcher/` in this skill's base directory, which is
shown when the skill loads. Below, `<skill-dir>` means that absolute path.

## Prerequisites (check once per session)

- `claude` is logged in. The launcher picks the mode itself: **bare** (`claude --bare`) when
  `ANTHROPIC_API_KEY` is set, and **lean** when the user is on a subscription login (Pro, Max,
  Team or Enterprise). Lean runs the same minimal profile with the project's CLAUDE.md, AGENTS.md
  and rules excluded. Both are fine. Do not ask the user for an API key when they are on a subscription.
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
2. **`.lean-worker/profiles.json`** holds the named worker profiles (model, effort, tools,
   pre-approved commands, budget). Start from `<skill-dir>/templates/profiles.json`. Replace
   the `{build command}` and `{test command}` placeholders with the project's real commands,
   and add a profile for any task class the project needs, such as a slow integration-test suite
   or a code generator.

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

In lean mode (subscription login), the worker gets none of the user's personal integrations:
CLAUDE.md at every level, auto memory, user/project/plugin hooks, output style, skills and MCP
are all excluded. Organisation-managed hooks still run. Use `--keep-hooks` or `"keepHooks": true`
in a profile only when a task needs one of the user's own hooks.

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
model:    claude-sonnet-5, effort medium, profile code, mode bare
work:     12 turns, 12 API calls, 3m41s
cost:     $0.4123 (list price reported by Claude Code)
tokens:   input 310 | cache write 21,004 | cache read 402,118 | output 9,870 (thinking 3,200)
context:  first call 4,812 | peak 38,420
--- worker report ---
...
```

- Read only this block. Do not read `stream.jsonl` or the whole diff into this session; that
  re-grows the context you are keeping small.
- Verify the done-criterion yourself with one command. The worker saying it is done is not verification.
- Tell the user the status and the cost line in one or two sentences.
- For a closer look, run `git diff --stat`, then read only the files that matter.

## 5. When it fails

- The worker's session is not kept, so do not try to continue it. Sharpen `task.md` (goal,
  start-here paths, scope) or split the task, and run again. Each run gets its own directory.
- If the cause is general (a missing command, an unknown convention), fix `project.md` or the
  profile instead of the task.
- `status: error` with `reason: api_error` and "Not logged in" means the worker could not
  authenticate. In bare mode the key is missing or invalid. In lean mode `claude` is not logged
  in: ask the user to run `claude` and `/login`.
- On a subscription, the `cost:` line is the list-price equivalent, not a bill; the tokens count
  against the plan's usage limits. Report it as such.
- If the summary reports permission denials, add the needed commands to the profile's `allowedTools`.

## Review run (optional)

Run the `review` profile with the task "try to refute that the change meets <done-criterion>;
list findings with file:line".

## Records

Every run leaves `.lean-worker/runs/<stamp>-<name>/` containing `task.md`, `system.md` (the
notes the worker actually received), `command.txt`, `stream.jsonl`, `summary.json` and
`report.md`. It also appends one JSON line to `.lean-worker/runs.jsonl` with the profile,
model, effort, status, turns, API calls, cost, token split, and first-call and peak context.
