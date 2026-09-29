# Installing lean-worker: instructions for an AI agent

These instructions are for a Claude Code session that a user has asked to install this skill,
for example: *"Install the lean-worker skill for this project, following
https://github.com/sy11a/claude-lean-worker/blob/main/INSTALL.md"*.

Follow the steps in order. Each step says what to run, what counts as success, and when to stop
and ask the user. Do not skip the questions: two steps change the user's configuration.

Throughout, **project root** means the root of the repository the user is working in, which is
normally your current working directory. **Repo copy** means the downloaded copy of this
repository.

---

## Step 1: Get the repository

Clone it into a temporary folder, not into the project:

```
git clone --depth 1 https://github.com/sy11a/claude-lean-worker.git "<temp-dir>/claude-lean-worker"
```

- On Windows, use `$env:TEMP` (PowerShell) or `$TEMP` (Git Bash) for `<temp-dir>`.
- **If the clone fails** (network policy, proxy, GitHub blocked), stop. Ask the user to download
  the repository ZIP and tell you where they extracted it. Do not look for workarounds around
  the network policy.

Success: `<repo copy>/install.ps1` and `<repo copy>/skills/lean-worker/SKILL.md` exist.

## Step 2: Check prerequisites

Run each command and report the result to the user in one short list:

| Check | Command | Required |
|---|---|---|
| Claude Code supports `--bare` | `claude --help` (look for `--bare`) | yes: stop and tell the user to update Claude Code |
| .NET SDK 8 or newer | `dotnet --list-sdks` | yes: stop and tell the user to install it |
| API key in the environment | check whether `ANTHROPIC_API_KEY` is set. **Never print its value** | no: note it for step 7 |
| PowerShell | `powershell -NoProfile -Command "$PSVersionTable.PSVersion.ToString()"` (or `pwsh`) | yes |

## Step 3: Ask the user two questions

Ask them together, in one message, and wait for the answer:

1. **Where should the skill be installed?**
   - (a) For the user, in `%USERPROFILE%\.claude\skills`, so it is available in every project.
     This is the recommended option.
   - (b) For this project only, in `<project root>\.claude\skills`, which can be committed and shared with the team.
2. **May the installer add a permission rule to `<project root>\.claude\settings.json`?** The rule
   is `Bash(dotnet run --project:*)`, and it lets the orchestrator start workers without a prompt
   each time.
   - The installer backs the file up first as `settings.json.bak-<stamp>`.
   - It rewrites the file as normalised JSON, so the existing formatting is lost.
   - If the user says no, the installer skips this step and the user approves each launch.

## Step 4: Run the installer

Run it from anywhere, pointing at the project root. On Windows the shell tool may be Git Bash;
call PowerShell explicitly:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<repo copy>/install.ps1" -ProjectPath "<project root>" [-Scope Project] [-SkipPermission]
```

- Add `-Scope Project` if the user chose (b) in question 1.
- Add `-SkipPermission` if the user said no to question 2.
- Use `pwsh` instead of `powershell.exe` if Windows PowerShell is not available.
- **If `-ExecutionPolicy Bypass` is refused** (organisation policy), stop and tell the user.
  Do not work around it.

Read the output. Success means:

- every line is `ok` or `WARN`, and the last section is `== Done`;
- `.lean-worker\project.md` and `.lean-worker\profiles.json` exist in the project root;
- `.gitignore` covers `.lean-worker/runs/`, `.lean-worker/inbox/` and `.lean-worker/runs.jsonl`.

If a line says `FAIL`, stop. Report that line to the user and follow what it says.

## Step 5: Fill in `.lean-worker/project.md`

This file is the context every worker receives, and it is paid for on every worker API call.
**Target: under ~2,000 tokens.** Delete the quoted template block at the top, then replace every `{...}` placeholder:

1. **Stack and layout:** read the build files (`*.sln`, `*.csproj`, `Directory.Build.props`,
   `package.json`, `pom.xml`, … whatever exists), the top-level README and CLAUDE.md, and the CI
   config. Write down the languages and frameworks, and where the main code, tests and tools live.
2. **Commands:** find the real build, test-all, test-one and lint/format commands. Prefer the ones
   CI uses. Do not run long builds or test suites just to check a command; say which ones you
   have not verified.
3. **Conventions:** only rules that matter when changing code, such as naming, error handling, test
   style or the architecture layering the codebase enforces. Two to six bullets.
4. **Never:** keep the defaults (no commit or push; no edits to CI, dependency versions, secrets or
   config) and add what the codebase shows is sensitive, such as generated code folders, migrations
   or public API contracts.
5. **Where to look:** link long documents by path. Do not paste them.

Anything you cannot determine from the repository, **ask the user** instead of guessing. Keep
it to one message with at most five questions.

## Step 6: Fill in `.lean-worker/profiles.json`

- In the `code` profile, replace `Bash({build command}:*)` and `Bash({test command}:*)` with
  the project's real command prefixes, e.g. `Bash(dotnet test:*)` or `Bash(npm run test:*)`.
  Add any other commands a coding worker needs routinely, such as a formatter or a code
  generator. Keep the list short.
- Keep the other profiles (`read`, `edit`, `research`, `review`) unless the user wants changes.
- If the project has an obvious extra task class (a slow integration-test suite, a separate
  frontend), propose a profile for it. Add it only if the user agrees.
- The file must stay valid JSON.

Then show the user both files, or a summary of what you filled in, and say plainly:
**"These files are yours: please read them and edit anything that is wrong."**

## Step 7: Smoke test

Ask the user before running this. It makes one real API call to Haiku with a budget of $0.10.

From the project root:

```
dotnet run --project "<skill-dir>/launcher" -c Release -- --task ".lean-worker/inbox/smoke-test/task.md" --model claude-haiku-4-5 --effort low --tools Read --max-budget-usd 0.1
```

Before running it, create `.lean-worker/inbox/smoke-test/task.md` containing:
`Do not read or change any file. Reply with exactly one line: lean-worker smoke test OK`.

`<skill-dir>` is where the skill was installed: `%USERPROFILE%\.claude\skills\lean-worker`, or
`<project root>\.claude\skills\lean-worker`.

- **If `ANTHROPIC_API_KEY` is not set:** tell the user. `--bare` cannot run without it. Offer two
  things: they set the key and restart the terminal, or you run the smoke test once with
  `--no-bare` so the installation itself is proven.
- **Success:** the block starts with `LEAN-WORKER RESULT`, shows `status: success`, and the worker
  report says `lean-worker smoke test OK`. Tell the user the cost and first-call context from
  the block.

## Step 8: Finish

1. Delete the temporary clone from step 1.
2. Tell the user, in a few lines:
   - where the skill is installed, and whether the permission rule was added;
   - what went into `project.md` and `profiles.json`, and what still needs their review;
   - the smoke test result, with its cost;
   - **that they must restart Claude Code (or start a new session) before `/lean-worker` is available;**
   - how to use it: `/lean-worker <what to do>; done when <command> passes`.
3. Do not commit anything. Whether `.lean-worker/project.md`, `profiles.json` and
   `.claude/settings.json` get committed is the user's decision. Recommend committing the first two.

---

## Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| `LEAN-WORKER LAUNCH FAILED: ANTHROPIC_API_KEY is not set` | `--bare` reads only the key | Set the key, or use `--claude-settings <file>` with an `apiKeyHelper`, or `--no-bare` |
| `status: error`, `reason: api_error`, "Not logged in" | Key missing or invalid in the worker's environment | Check that the key is set in the environment Claude Code was started from |
| `'claude' is not on PATH` from the launcher | Claude Code is installed for another shell | Add its folder to PATH, or reinstall with the native installer |
| Permission prompts on every launch | Rule not added, or a different command shape | Add `Bash(dotnet run --project:*)` to `.claude/settings.json` `permissions.allow` |
| `WARNING: n permission denial(s)` in a result | The worker needed a command not pre-approved | Add that command prefix to the profile's `allowedTools` |
| Build error on the first run | SDK older than 8, or a corrupted copy | Check `dotnet --list-sdks`, then re-run `install.ps1` |
