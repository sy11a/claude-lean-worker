---
name: lean-worker
description: Delegate one well-scoped coding task from an orchestrating Claude Code session to a separate minimal-context worker process (`claude -p --bare`) and get back its report plus exact token usage and cost. Use when a task has a clear goal and a done-criterion you can check with a command (implement a change, write tests, a mechanical refactor, a focused investigation), and you want it done without loading this session's full project context, skills and MCP servers into the worker.
---

# Lean worker

You are the orchestrator. For a well-scoped task you do not do the work in this session,
and you do not use an in-session subagent: a subagent inherits the project's CLAUDE.md,
the skill listing, every MCP server and every tool schema, which is often 40-60k tokens
before it starts, re-read on every call. A `--bare` worker loads nothing on its own.
It gets only the system notes and the task you write, so it usually starts at a few
thousand tokens.

The launcher script is `scripts/Invoke-LeanWorker.ps1` in this skill's base directory
(shown when this skill loads). Use that absolute path below as `<skill-dir>`.

## Prerequisites (check once per session)

- `ANTHROPIC_API_KEY` is set in the environment. `--bare` never reads OAuth or the keychain.
  If it is missing, tell the user. Pass `-NoBare` only if the user agrees; it runs the same
  lean profile without `--bare`, which is somewhat larger.
- `claude` is on PATH, and `claude --help` lists `--bare`.

## 1. Write the task

Create `.lean-worker/inbox/<task-name>/task.md` from `templates/task.md`. It must have:

- **Goal:** one paragraph.
- **Start here:** the files to read first, as paths. This saves the worker exploring.
- **Done when:** a command whose output proves it, e.g. `dotnet test` exits 0.
- **Boundaries:** what must not be touched. Never commit, push or change config.
- **Report format:** at most 30 lines, covering what was done, files changed, how it was
  verified, what failed, and open questions.

Optionally create `system.md` next to it from `templates/system.md`. Put in it only the
rules this task needs: stack, code style, build and test commands, prohibitions. Aim for
1-3k tokens. Do not paste the whole CLAUDE.md.

## 2. Pick the profile

| Task class | -Model | -Effort | -Tools |
|---|---|---|---|
| Read / search code | claude-haiku-4-5 | low | Read,Glob,Grep |
| Mechanical edit from a precise description | claude-sonnet-5 | low | Read,Edit,Glob,Grep |
| Code + tests | claude-sonnet-5 | medium | Read,Edit,Write,Glob,Grep,Bash |
| Docs research | claude-sonnet-5 | medium | Read,Glob,Grep,WebFetch,WebSearch |
| Design or hard judgement | do it yourself, not in a worker | | |

- `-AllowedTools` pre-approves exactly the shell commands the task needs,
  e.g. `'Bash(dotnet build:*)','Bash(dotnet test:*)','Bash(git diff:*)','Bash(git status:*)'`.
  Anything else is denied, and the summary reports the denials.
- `-McpConfig <file.json>` only if the task needs a specific MCP server. By default no MCP
  server is loaded.
- `-MaxBudgetUsd` is a safety cap (default 2). If it triggers, the task was cut too large.

## 3. Run it

Run it with the shell tool **in the background**, because a worker can take longer than
the tool's timeout. Then wait for the completion notification. Do not poll.

```
powershell -NoProfile -ExecutionPolicy Bypass -File "<skill-dir>/scripts/Invoke-LeanWorker.ps1" -TaskFile ".lean-worker/inbox/<task-name>/task.md" -SystemFile ".lean-worker/inbox/<task-name>/system.md" -Model claude-sonnet-5 -Effort medium -AllowedTools 'Bash(dotnet build:*)','Bash(dotnet test:*)'
```

Use `pwsh` instead of `powershell` if it is installed. If the execution policy is blocked
by your organisation, tell the user rather than working around it.

## 4. Read the result

The script prints a block like this, and nothing else:

```
LEAN-WORKER RESULT
run:      .lean-worker/runs/20260929-132609-add-greeting
status:   success  (subtype=success, reason=completed, exit=0)
model:    claude-sonnet-5, effort medium, bare=True
work:     12 turns, 12 API calls, 3m41s
cost:     $0.4123 (list price reported by Claude Code)
tokens:   input 310 | cache write 21,004 | cache read 402,118 | output 9,870 (thinking 3,200)
context:  first call 4,812 | peak 38,420
--- worker report ---
...
```

- Read only this block. Do not read `stream.jsonl` or the whole diff into this session,
  because that re-grows the context you are trying to keep small.
- Verify the done-criterion yourself with one command, e.g. `dotnet test`. The worker's
  word is not verification.
- Tell the user the status and the cost line in one or two sentences.
- For a closer look, run `git diff --stat` and then read only the files that matter.

## 5. When it fails

- Do not continue the worker's session; it is not kept. Improve `task.md` (a sharper goal,
  more "Start here" paths, a smaller scope) or split the task. Then run it again. Each run
  gets its own run directory.
- `status: error` with `reason: api_error` and "Not logged in" means the API key is missing.
- If permission denials are reported, add the needed commands to `-AllowedTools`.

## Review run (optional)

To check a worker's change, run a second worker with the task "try to refute that the
change meets <done-criterion>; list findings with file:line", with
`-Tools Read,Glob,Grep,Bash` and `-AllowedTools 'Bash(git diff:*)'`.

## Records

Every run leaves `.lean-worker/runs/<stamp>-<name>/`, containing `task.md`, `system.md`,
`command.txt`, `stream.jsonl`, `summary.json` and `report.md`. It also appends one line to
`.lean-worker/runs.jsonl`: model, effort, status, turns, API calls, cost, the token split,
first-call and peak context. `.lean-worker/` should be in `.gitignore`.
