# lean-worker: budget wrap-up, handoff and continuation (spec)

Target repository: `claude-lean-worker` (https://github.com/sy11a/claude-lean-worker), upstream base commit `13c9fa3` (2026-09-29).
Source: 7 photographs (`1.jpg`..`7.jpg`, kept outside the repo in `~/Repository/lean-feature-raw/`) of an implementation report written on another PC for that machine's fork. The report was transcribed on 2026-09-29 with the method used for the debrief snapshot (debrief ADR-0003): photo overlaps stitched, unreadable or cut-off text marked `[?]`, nothing invented. Editorial notes are in *[italic brackets]*.

The document has four parts:

- **Part I**: instructions to the implementing agent. Read this first.
- **Part II**: the source report, verbatim. It describes a Windows-only, single-client, Claude-only implementation.
- **Part III**: adaptation requirements. These override Part II where the two disagree.
- **Part IV**: review findings found while transcribing, and the operator's decisions.

**Operator decisions (2026-09-29)**, already applied in Part III:

- D1. The wrap-up hook is C#, a subcommand of the launcher. No node. (R1)
- D2. State stays in the repo, in `<project>/.lean-worker/`, shared by Claude Code, opencode and any other coding tool. Part II's user-scope `~/.claude/lean-worker/` is dropped. (R2, R3)
- D3. On subscriptions and coding plans, wrap-up uses the list-price equivalent. No plan-unit limits. (R4)
- D4. opencode, headless and manual, is in the first version. (R5)
- D5. This document is the tracked spec in `claude-lean-worker/docs/specs/`.

---

## Part I: Instructions to the implementing agent

1. **Review before building.** Treat Part II as evidence of a working design, not as a patch to apply. Before any code change, write a review of Part II against the current upstream code and against Part III. List what carries over, what changes and what is dropped, and put that list in front of the operator. The design decisions in Part II ("Chosen / Rejected") are inputs to that review, not settled facts for our setup.
2. **Part III wins over Part II.** Part II assumed Windows, one client repository, Claude-only models, fixed prices and Claude Code as the only runtime. None of that holds here.
3. **Upstream, not a local patch.** The report delivers a patch that `install.ps1` overwrites on re-run (Part II §3 step 5, §8 last bullet). Here the feature goes into the repository itself, behind options, with the upstream defaults unchanged for people who don't opt in.
4. **Keep the fidelity marks.** Where Part II has `[?]`, look at the upstream code or ask. Do not guess.
5. **Machine-local references in Part II do not exist here:** `brain/tools/session-cost.js`, the memory `reference-transcript-usage-census-method`, the user hook `block-secret-reads.sh`, "Step 11a's context hook", ADR 0016 and the backup folder `~/.claude/brain/_archive/...`. Recreate what the feature needs from them (Part III R5, R6); leave the rest out.

---

## Part II: Source report (verbatim transcription)

*[Context visible in the photos' sticky headers: the operator's prompts on the other PC were "yes, add wrap up hook, maybe we need to continue the task with fresh session by transfer handoff to it after limit" and "describe what your changes are - to the lean-worker - so I would read the prompt, architecture and idea on other pc".]*

### lean-worker: local changes (implementation report)

Upstream base: https://github.com/sy11a/claude-lean-worker at commit 13c9fa3 (2026-09-29). Everything below is a change on top of that commit. You can rebuild it on another PC from this report alone.

#### 1. The idea

lean-worker hands one well-scoped task to a separate `claude -p` process with minimal context. You set a spending cap per profile (`maxBudgetUsd`), and the launcher passes it to claude as `--max-budget-usd`. The upstream version has two gaps:

1. Nothing lives outside the repo. Upstream keeps worker state in `<project>/.lean-worker/` and adds `.gitignore` entries and `.claude/settings.json` rules to the repo. We keep all of it under `~/.claude/`.
2. The cap stops the worker blind. When the budget runs out, the worker stops mid-task and writes no report. The files it edited stay on disk, but nobody records what is done and what is left, and the session can't be resumed. Our fix:
   - At 80% of the budget, a hook blocks every tool call. The worker's only option is then to write a structured HANDOFF as its final message.
   - A new `--continue-from <run-dir>` flag starts a fresh worker with the original task plus that handoff. It deliberately doesn't resume the old session, because resuming would re-read the whole large context.
   - Continuing waits for the operator's go (cost control).

#### 2. Architecture

```
orchestrator session
  └ dotnet run launcher -- --task <rr>/inbox/<t>/task.md --runs-root <rr> --profile code
     ├ writes <rr>/runs/<stamp>-<t>/settings.json:
     │    claudeMdExcludes, autoMemoryEnabled=false, outputStyle=default   (upstream)
     │    env: LEAN_WORKER_WRAPUP_USD / _BUDGET_USD / _RUN_DIR             (new)
     │    hooks.PreToolUse += node budget-wrapup.js                         (new)
     ├ starts: claude -p --settings <that file> --max-budget-usd B ...
     │    (WITHOUT --no-session-persistence when wrap-up is on → transcript exists)
     │
     │  worker, before each tool call → budget-wrapup.js:
     │    reads transcript_path (hook stdin) → sums usage × price table
     │    spent ≥ B × wrapUpAt → writes <run>/wrapup.json, returns deny + HANDOFF instruction
     │    worker can no longer use tools → last message = HANDOFF → report.md
     │
     └ after exit: wrapup.json exists → status "wrapped-up", exit 3, prints "continue: --continue-from <run>"

orchestrator → operator: spend so far + Remaining → on "go":
  dotnet run launcher -- --continue-from <run> --runs-root <rr>
     task.md = original task (cut at "## Continuation") + "## Continuation" + previous report.md
     profile defaults to the previous run's; full budget again
```

**Design decisions and the alternatives rejected**

Where the hook reads spend from
- Chosen: the session transcript (`transcript_path` in the hook's input). It needs session persistence switched on. Step 11a's context hook will read the same source, and saved worker sessions also make their spend visible to the transcript-based cost tools.
- Rejected: keeping persistence off, flushing `stream.jsonl` after every line, and pointing the hook at it.

Resuming vs a fresh worker
- Chosen: a fresh worker plus the handoff. It starts from a small context.
- Rejected: `--resume`, which re-reads the entire previous context.

How the hook is registered
- Chosen: the launcher injects it into the per-run settings, so it runs only in workers.
- Rejected: registering it in the user `settings.json`. It would then start node on every tool call in every session.

When wrap-up is active
- Only in lean mode with hooks on. Bare mode (`claude --bare`) skips hooks entirely.
- In lean mode, the launcher's default `disableAllHooks` would also disable the injected hook, which is why every profile sets `keepHooks: true`.

Facts measured on this machine (Claude Code 2.1.284)
- With `--no-session-persistence`, a hook still receives a `transcript_path`, but the file doesn't exist.
- Hooks do receive the env block from a `--settings` file.
- The transcript doesn't record cost, only usage, so the hook needs a price table.
- A matcher of `".*"` matches every tool.
- Settings hooks and user hooks both run: `block-secret-reads.sh` still fired next to the injected hook.
- Passing `--claude-settings` to the launcher switches `--mode auto` to bare (upstream line ~94). Add `--mode lean` if you ever use it.

#### 3. Installation (user scope, nothing inside a repo)

1. Clone the upstream repo into a temp folder. Check prerequisites: `claude --help` lists `--bare`, `dotnet --list-sdks` shows 8 or newer, PowerShell is present, and node is on PATH (the hook needs it).
2. Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1` without `-ProjectPath`. That copies the skill to `%USERPROFILE%\.claude\skills\lean-worker` and builds it.
3. Apply sections 4-6, then run `dotnet build -c Release` in `...\lean-worker\launcher`. The build treats warnings as errors.
4. Create `%USERPROFILE%\.claude\lean-worker\<repo-folder-name>\project.md` and `profiles.json` (section 7).
5. Delete the temp clone. Re-running upstream `install.ps1` later overwrites `Program.cs` and `.csproj`, which drops this patch.

#### 4. New file: `launcher/budget-wrapup.js`

```js
// PreToolUse hook the launcher injects into lean workers. Once the transcript's spend passes
// LEAN_WORKER_WRAPUP_USD it denies every tool call, so the worker's last message is a handoff.
const fs = require('fs');
const path = require('path');

// $ per MTok: input, output, cache read. Cache write = input x1.25 (5m) / x2 (1h).
// Copied from brain/tools/session-cost.js (claude-api skill, 2026-09-25). Unknown models price as the dearest.
const PRICES = {
    'claude-opus-5-5': [4, 20, 0.20],
    'claude-sonnet-5-5': [2, 10, 0.20],
    'claude-haiku-4-5': [1, 5, 0.10],
    'claude-fable-5-1': [10, 50, 0.25],
};
const FALLBACK = PRICES['claude-fable-5-1'];

function priceFor(model) {
    const key = Object.keys(PRICES).find(k => model && model.startsWith(k));
    return key ? PRICES[key] : FALLBACK;
}

// Dedupes by message.id, last record wins (see memory reference-transcript-usage-census-method).
function spentUsd(transcript) {
    const byId = new Map();
    for (const line of fs.readFileSync(transcript, 'utf8').split('\n')) {
        if (!line.includes('"usage"')) {
            continue;
        }
        let rec;
        try {
            rec = JSON.parse(line);
        } catch {
            continue;
        }
        const msg = rec.message;
        if (rec.type === 'assistant' && msg && msg.id && msg.usage) {
            byId.set(msg.id, msg);
        }
    }
    let usd = 0;
    for (const { model, usage } of byId.values()) {
        const [inp, out, read] = priceFor(model);
        const cc = usage.cache_creation || {};
        const w1h = cc.ephemeral_1h_input_tokens || 0;
        const w5m = cc.ephemeral_5m_input_tokens != null
            ? cc.ephemeral_5m_input_tokens
            : (usage.cache_creation_input_tokens || 0) - w1h;
        usd += ((usage.input_tokens || 0) * inp + (usage.output_tokens || 0) * out
            + (usage.cache_read_input_tokens || 0) * read + w5m * inp * 1.25 + w1h * inp * 2) / 1e6;
    }
    return usd;
}

function main(input) {
    const threshold = parseFloat(process.env.LEAN_WORKER_WRAPUP_USD);
    const transcript = input.transcript_path;
    if (!(threshold > 0) || !transcript || !fs.existsSync(transcript)) {
        return;
    }
    const spent = spentUsd(transcript);
    if (spent < threshold) {
        return;
    }
    const budget = process.env.LEAN_WORKER_BUDGET_USD || '?';
    const runDir = process.env.LEAN_WORKER_RUN_DIR;
    const marker = runDir && path.join(runDir, 'wrapup.json');
    if (marker && !fs.existsSync(marker)) {
        fs.writeFileSync(marker, JSON.stringify({ at: new Date().toISOString(), spentUsd: spent, thresholdUsd: /* [?] cut at the frame edge; presumably threshold */,
budgetUsd: Number(budget), deniedTool: input.tool_name }, null, 2));
    }
    const reason = `Budget nearly spent (about $${spent.toFixed(4)} of $${budget}). Tool calls are now blocked. `
        + 'Stop working. Make your next message the final report, and put this block first:\n'
        + 'HANDOFF\n'
        + '- Done: what is finished and verified\n'
        + '- Remaining: what is left, in order\n'
        + '- Files touched: paths, and whether each edit is complete or half-done\n'
        + '- State: does it build, do the tests pass, as far as you know\n'
        + '- Next step: the first thing a fresh worker should do';
    process.stdout.write(JSON.stringify({
        hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason /* [?] cut at the frame edge; presumably ": reason" */ }
    }));
}

let raw = '';
process.stdin.on('data', d => raw += d).on('end', () => {
    try {
        main(JSON.parse(raw));
    } catch {
        // A broken hook must never block the worker; the launcher's hard cap still applies.
    }
});
```

Check the prices on the other PC against the current list prices before relying on the threshold. Here the estimate came out at $0.012 against Claude's own $0.0124.

#### 5. `launcher/LeanWorker.csproj`

Add this before `</Project>`, so the hook is copied next to the exe and the launcher can find it through `AppContext.BaseDirectory`:

```xml
<ItemGroup>
  <None Include="budget-wrapup.js" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

#### 6. `launcher/Program.cs` (the upstream file uses 4-space indentation)

**6.1 Header comment, exit codes**

```csharp
// Exit codes: 0 = worker finished without error, 1 = worker reported an error, 2 = launcher failed,
// 3 = worker wrapped up near its budget and left a handoff (continue with --continue-from <run-dir>).
```

**6.2 Constant, next to `PermissionModes`**

```csharp
private const string ContinuationHeading = "## Continuation";
```

**6.3 At the start of "resolve profile", after `var runsRoot = ...;`:** load the previous run's summary, and default the profile to the one that run used.

```csharp
JsonObject? prevSummary = null;
if (o.ContinueFrom is not null)
{
    var prevSummaryPath = Path.Combine(o.ContinueFrom, "summary.json");
    if (!File.Exists(prevSummaryPath) || !File.Exists(Path.Combine(o.ContinueFrom, "report.md")))
    {
        throw new LaunchException($"--continue-from needs a finished run dir (summary.json + report.md): {o.ContinueFrom}");
    }
    prevSummary = JsonNode.Parse(File.ReadAllText(prevSummaryPath, Utf8))?.AsObject();
}
JsonObject? profile = null;
var profileName = o.Profile ?? Str(prevSummary, "profile");
```

**6.4 After `var budget = ...`**

```csharp
// Share of the budget after which the wrap-up hook blocks tools; 0 turns it off.
var wrapUpAt = o.WrapUpAt ?? Dec(profile, "wrapUpAt") ?? 0.8m;
if (wrapUpAt is < 0 or >= 1) throw new LaunchException($"invalid wrap-up share {wrapUpAt} (0 = off, else below 1)");
```

**6.5 Input validation:** `--task` becomes optional when `--continue-from` is given.

```csharp
if (o.TaskFile is null && o.ContinueFrom is null) throw new LaunchException("--task <file> or --continue-from <run-dir> is required");
if (o.TaskFile is not null && !File.Exists(o.TaskFile)) throw new LaunchException($"task file not found: {o.TaskFile}");
```

**6.6 Replace the `taskPath` / `name` / `File.Copy` block.** Continuation assembles the task text from the previous run; the task is written as text, not copied.

```csharp
string taskText, name;
if (o.ContinueFrom is not null)
{
    // Fresh worker, not a resumed session: the original task plus the previous worker's handoff.
    var prevTask = File.ReadAllText(Path.Combine(o.ContinueFrom, "task.md"), Utf8);
    var cut = prevTask.IndexOf(ContinuationHeading, StringComparison.Ordinal);
    if (cut >= 0) prevTask = prevTask[..cut];
    taskText = prevTask.TrimEnd() + Environment.NewLine + Environment.NewLine + ContinuationHeading +
        Environment.NewLine + Environment.NewLine +
        "A previous worker on this task stopped when its budget ran low. Its handoff is below. Check the current " +
        "state first (for example `git status` and `git diff --stat`) and do not redo finished work." +
        Environment.NewLine + Environment.NewLine + File.ReadAllText(Path.Combine(o.ContinueFrom, "report.md"), Utf8).Trim() + Environment.NewLine;
    name = o.Name ?? Str(prevSummary, "name") ?? "continuation";
}
else
{
    var taskPath = Path.GetFullPath(o.TaskFile!);
    taskText = File.ReadAllText(taskPath, Utf8);
    name = o.Name ?? new DirectoryInfo(Path.GetDirectoryName(taskPath)!).Name;
}
// (safeName / started / runDir / CreateDirectory unchanged)
File.WriteAllText(Path.Combine(runDir, "task.md"), taskText, Utf8);   // replaces File.Copy
```

**6.7 Before `var a = new List<string> { "-p" };`**

```csharp
// The hook reads spend from the session transcript, and bare mode skips hooks altogether.
var wrapUp = wrapUpAt > 0 && mode == "lean" && !noHooks;
```

**6.8 In the lean-mode settings block, right after `settings["outputStyle"] = "default";`:** inject the env vars and the hook.

```csharp
if (wrapUp)
{
    // Near the budget the hook blocks tools, so the worker spends its last turn on a handoff.
    if (settings["env"] is not JsonObject hookEnv) settings["env"] = hookEnv = new JsonObject();
    hookEnv["LEAN_WORKER_WRAPUP_USD"] = (budget * wrapUpAt).ToString(CultureInfo.InvariantCulture);
    hookEnv["LEAN_WORKER_BUDGET_USD"] = budget.ToString(CultureInfo.InvariantCulture);
    hookEnv["LEAN_WORKER_RUN_DIR"] = Path.GetFullPath(runDir);
    if (settings["hooks"] is not JsonObject hooks) settings["hooks"] = hooks = new JsonObject();
    if (hooks["PreToolUse"] is not JsonArray preToolUse) hooks["PreToolUse"] = preToolUse = new JsonArray();
    var hookScript = Path.Combine(AppContext.BaseDirectory, "budget-wrapup.js").Replace('\\', '/');
    preToolUse.Add(new JsonObject
    {
        ["matcher"] = ".*",
        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"node \"{hookScript}\"" }),
    });
}
```

**6.9 Arguments:** remove `"--no-session-persistence"` from the fixed `AddRange`, then add it back only when wrap-up is off.

```csharp
a.AddRange(["--permission-mode", permissionMode,
            "--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture),
            "--output-format", "stream-json", "--verbose"]);
// Session persistence stays on: the wrap-up hook reads the transcript, and worker spend shows up
// in the transcript-based cost tools.
if (!wrapUp) a.Add("--no-session-persistence");
```

**6.10** `RunClaude` receives the text: replace `File.ReadAllText(taskPath, Utf8)` with `taskText`.

**6.11 After the `var status = ...` expression**

```csharp
var wrappedUp = File.Exists(Path.Combine(runDir, "wrapup.json"));
if (wrappedUp && status is "success" or "error") status = "wrapped-up";
```

**6.12 `summary` JsonObject: four new fields.**

```csharp
["budget_usd"] = budget,
["wrap_up_usd"] = wrapUp ? budget * wrapUpAt : null,
["wrapped_up"] = wrappedUp,
["continued_from"] = o.ContinueFrom is null ? null : Path.GetFullPath(o.ContinueFrom),
```

**6.13 Output:** a `budget:` line after `cost:`, and a `continue:` line that replaces the denial warning when the run wrapped up. The hook's denials would otherwise trigger the misleading "add to allowedTools" warning.

```csharp
var wrapUpText = wrapUp ? $"wrap-up at ${(budget * wrapUpAt).ToString("0.####", ic)}{(wrappedUp ? " (triggered)" : "")}" : "wrap-up off";
w.WriteLine($"budget:   ${budget.ToString("0.####", ic)}, {wrapUpText}");
...
if (wrappedUp) w.WriteLine($"continue: --continue-from \"{Path.GetFullPath(runDir)}\" (fresh worker, original task + this handoff)");
else if (denials > 0) w.WriteLine(/* upstream WARNING line unchanged */);
```

*[The `...` is in the original.]*

**6.14 Return value**

```csharp
return status switch { "success" => 0, "wrapped-up" => 3, _ => 1 };
```

**6.15 Options**

- Fields: add `ContinueFrom` to the `string?` list and `WrapUpAt` next to `MaxBudgetUsd` (`decimal?`).
- Parse cases:
  ```csharp
  case "--wrap-up-at": o.WrapUpAt = decimal.Parse(Next(), CultureInfo.InvariantCulture); break;
  case "--continue-from": o.ContinueFrom = Next(); break;
  ```
- Usage text, after `--max-budget-usd`:
  ```
  --wrap-up-at <share>       lean mode with hooks: past this share of the budget tools are blocked and the
                             worker writes a handoff (default 0.8; 0 = off; profile key "wrapUpAt")
  --continue-from <run-dir>  fresh worker on a wrapped-up run: its task.md + its report as the handoff
                             (replaces --task; profile defaults to that run's)
  ```

#### 7. Configuration

`~/.claude/lean-worker/code/profiles.json`
- Dropped from the template: the `research` profile (web access is barred by ADR 0016) and `Bash(cat:*)` (Read covers it).
- Added: `keepHooks: true` on every profile.
- Models set to current ids.

```json
{
  "defaultProfile": "code",
  "profiles": {
    "read":   { "model": "claude-haiku-4-5",  "effort": "low",    "tools": ["Read","Glob","Grep"], "allowedTools": [], "keepHooks": true, "maxBudgetUsd": 0.5 },
    "edit":   { "model": "claude-sonnet-5-5", "effort": "low",    "tools": ["Read","Edit","Glob","Grep"], "allowedTools": [], "keepHooks": true, "maxBudgetUsd": 1 },
    "code":   { "model": "claude-sonnet-5-5", "effort": "medium", "tools": ["Read","Edit","Write","Glob","Grep","Bash"],
                "allowedTools": ["Bash(acubuild build:*)","Bash(dotnet build:*)","Bash(dotnet test:*)","Bash(git diff:*)","Bash(git status:*)","Bash(ls:*)","Bash(pwd)"],
                "keepHooks": true, "maxBudgetUsd": 3 },
    "review": { "model": "claude-sonnet-5-5", "effort": "medium", "tools": ["Read","Glob","Grep","Bash"], "allowedTools": ["Bash(git diff:*)","Bash(git log:*)"], "keepHooks": true, "maxBudgetUsd": 1 }
  }
}
```

`wrapUpAt` isn't set anywhere, so the default of 0.8 applies. Lower it in a profile whose tasks run with very large contexts.

`~/.claude/lean-worker/code/project.md` follows the upstream template with the quoted block removed. Its sections:
- Stack: C#/.NET, the PX platform, TS+HTML Modern UI, SQL.
- Layout: NetTools (kernel, higher risk), WebSites, CustomSolutions, ClientSideApps/FrontendSources, tests in `*.Tests`.
- Commands: `acubuild build`, and `dotnet test <csproj> --filter` for one project. Never the whole suite; commands not verified by running.
- Conventions: tabs; BOM, final newline and no trailing whitespace; keep existing line endings; reuse `PX.*`; `var`; braces on every new if/loop; XML-doc on public members; the DAC prompt file; AAA with FluentAssertions; inline Acuminator suppressions; comments at most two lines; English only.
- Never: commit, push or branch; destructive git; creating own files in the repo; CI, dependency or secrets edits; touching NetTools beyond the named files.
- Where to look: `PX.Api.TSBasedScreen/AGENTS.md`, `DACAnnotations.prompt.md`, `.editorconfig`.
- Work style: kept from the template.

#### 8. SKILL.md: section added after the line about `<skill-dir>`

```markdown
## Local adaptation (overrides the rest of this file)

- **Nothing goes inside a product repo.** Per-project state lives at
  `C:/Users/<user>/.claude/lean-worker/<repo-folder-name>/` (the `<runs-root>`), never at
  `<project>/.lean-worker/`. Every launch passes `--runs-root "<runs-root>"`, and task files go to
  `<runs-root>/inbox/<task-name>/task.md`. Wherever this file says `.lean-worker/`, read `<runs-root>/`.
  Never add `.gitignore` entries or `.claude/settings.json` rules to a repo. Configured today: `code`.
- **Profiles keep the user's hooks** (`"keepHooks": true`), because they are the secret-read and
  destructive-git guards. There is no `research` profile: ADR 0016 bars web access.
- Run from the task's worktree root, so the worker edits that worktree.
- **Budget wrap-up (local launcher patch).** Past `wrapUpAt` of the budget (default 0.8, profile key
  or `--wrap-up-at`, 0 = off) a PreToolUse hook (`launcher/budget-wrapup.js`) denies every tool call,
  so the worker's last message is a `HANDOFF` block (Done / Remaining / Files touched / State / Next
  step). The result shows `status: wrapped-up`, exit code 3, and a `continue:` line. Lean mode with
  hooks on only; the hook reads the session transcript, so these workers keep session persistence.
- **Continuing a wrapped-up run.** Tell the operator the spend so far and the handoff's Remaining
  list, and continue only on the operator's go (ADR 0016 rule 8). Then launch
  `--continue-from "<run-dir>" --runs-root "<runs-root>"` without `--task`: a fresh worker gets the
  original task plus that handoff, and the profile defaults to the previous run's. Each continuation
  gets the full budget again. Never resume the old session; it would re-read the whole context.
- Re-running upstream `install.ps1` overwrites `Program.cs` and `LeanWorker.csproj` and drops this patch.
```

#### 9. Acceptance tests (Haiku, about $0.05 in total)

Run each from a scratch cwd with `--runs-root <scratch>/rr --model claude-haiku-4-5 --effort low --keep-hooks --no-project-notes`.

1. **Wrap-up triggers.**
   - Task: "Call Glob 12 times, one per turn, patterns a\*..l\*, then reply DONE + count".
   - Flags: `--tools Glob --max-budget-usd 0.03 --wrap-up-at 0.3`.
   - Expect: `status: wrapped-up`, exit 3, `wrapup.json` in the run dir, a report that starts with HANDOFF, and no `no-session-persistence` in `command.txt`.
2. **Continuation.** Run `--continue-from "<run1>" --tools Glob --max-budget-usd 0.05`, with no `--task`.
   - Expect: success, only the remaining calls made (9, not 12), and `task.md` = original task + `## Continuation` + handoff.
3. **User guard hooks still run.** Put a dummy `dummy.env` in the cwd; task: "Read dummy.env; reply READ OK or BLOCKED + reason".
   - Expect: BLOCKED by your own secret-read hook.
4. **Wrap-up off.** Run with `--wrap-up-at 0`.
   - Expect: `budget: ..., wrap-up off`, `--no-session-persistence` back in `command.txt`, and no `LEAN_WORKER_*` in `settings.json`.
5. **Cost estimate.** Run `session-cost.js <session_id> <project-dir>` on the test 1 transcript. It should come out within a few percent of the launcher's `cost:` line.

#### 10. Known limits

- Report margin: the remaining 20% has to cover one final report turn. At a very large context on Sonnet it might not, and the hard cap then stops the run with no report. If that happens, lower `wrapUpAt` for that profile.
- Continuing a continuation: not tested. The code cuts the task at the first `## Continuation` heading, so a task that itself contains that heading would be cut early.
- Price table: a hand-copied table that goes stale when prices change. Unknown models are priced as the most expensive one, which only makes the wrap-up happen earlier.

On this PC the full patched files are backed up in `~/.claude/brain/_archive/cost-first-backups-2026-09-29/backup-step7/patched-wrapup/`. If you can copy files across, that is quicker than retyping from this report.

*[The last photo ends on a cut-off heading, "Changed files" `[?]`. Anything after it was not photographed.]*

---

## Part III: Adaptation requirements

These override Part II. Each requirement ends with what counts as done.

### R0. Review Part II before implementing

Write a short review (in the PR description, or `docs/` if the repo has one by then) that walks through Part II §1-§10 and marks each item **keep / change / drop**, with the reason tied to R1-R6 or to Part IV. The operator approves the review before code is written.

*Done when:* the review exists, every Part II section has a verdict, and the operator has approved it.

### R1. Linux first, Windows kept working

Part II was written on Windows. This machine is Linux (Fedora). Upstream is .NET and already builds and runs on Linux, and its README says `install.ps1` was tested under PowerShell 7 on Linux. The patch itself adds Windows-only assumptions:

- Paths: `%USERPROFILE%`, `C:/Users/<user>/...` and `...\lean-worker\launcher` become `~`/`$HOME`-based paths resolved in code (`Environment.GetFolderPath(SpecialFolder.UserProfile)`), never hard-coded separators.
- Installer: `powershell.exe -ExecutionPolicy Bypass` doesn't exist on Linux. Provide a POSIX `install.sh` with the same steps and flags as `install.ps1` (or make `pwsh install.ps1` the documented Linux route and test it). Either way the installer must install the hook file too and must not drop local configuration on re-run.
- Hook runtime (D1): the hook is C#, a launcher subcommand (for example `LeanWorker hook pre-tool`), started through the built launcher binary, not `dotnet run`, so every tool call doesn't pay a build check. `budget-wrapup.js` and the node prerequisite are dropped. The hook shares the price and usage code with the launcher's `cost:` line (R3). Watch its start-up time: it runs before every tool call, so measure it, and consider ReadyToRun or AOT if it is slow.
- Quoting: the hook command `node "<path>"` must survive a home path with spaces on both OSes.
- Line endings and encoding: new files LF, UTF-8 without BOM (match `.gitattributes`). The transcript parser splits on `\n` and must tolerate `\r\n`.
- Follow the debrief cross-platform rule: anything OS-bound (installer steps, the example guard hook, paths in SKILL.md) carries a binding per host OS, selected at run time, instead of being rewritten from one OS to the other.

*Done when:* the acceptance tests (R7) pass on Linux, and nothing in the change makes Windows fail to build or install.

### R2. Generalize away from the client

Part II was written for one client target (a .NET platform codebase with its own build tool, ADRs and conventions). None of that belongs in the repo.

- Section 7 (`profiles.json`, `project.md`) is an **example of a filled-in project**, not a template. The templates stay generic (`{build command}`, `{test command}`). Client names, build tools, directory names and ADR numbers must not appear.
- Client policies become generic, opt-in settings:
  - **State location (D2).** State stays in the repo, in `<project>/.lean-worker/` as upstream does. It is the single source for every coding tool: Claude Code and opencode orchestrators and workers read the same `project.md`, `profiles.json`, `prices.json`, inbox, runs and `runs.jsonl`. Nothing tool-specific goes in it (no `.claude/` or `.opencode/` paths). Part II's user-scope location is dropped. `--runs-root` stays as an override. Decide in R0 how worktrees share it: one `.lean-worker/` per worktree, or resolved to the main checkout.
  - **No web access** ("ADR 0016") becomes "the `research` profile is optional; delete it if your policy bars web access".
  - **Continue only on the operator's go** ("ADR 0016 rule 8") becomes the default SKILL.md behaviour for continuations: report spend so far and the Remaining list, then wait.
  - **keepHooks everywhere** was needed only because wrap-up depended on it (see Part IV F1). It must not become a template default.
- `block-secret-reads.sh` is the other PC's personal guard hook. Acceptance test 3 becomes "a user hook of your own still runs when `keepHooks` is on", with a tiny sample hook shipped for the test.

*Done when:* `grep -ri` for the client's names, build tool and ADR numbers over the repo finds nothing, and a fresh install on an unrelated project works with the stock templates.

### R3. Prices are configuration, not code

Part II hard-codes four Claude prices in `budget-wrapup.js` and prices unknown models as the dearest. Prices change and new models appear, so:

- Put prices in a **price file** (JSON). Defaults ship as `<skill-dir>/prices.json`, and the project's `.lean-worker/prices.json` (D2) is merged over them; it is shared by every tool and never overwritten by the installer. An optional personal override is allowed only in a tool-neutral place (`${XDG_CONFIG_HOME:-~/.config}/lean-worker/prices.json`), not under `~/.claude/`.
- Per model entry: an id or prefix to match, provider, currency, `input`, `output`, `cacheRead`, and cache-write prices (either absolute per TTL or multipliers of input), an `asOf` date and a source URL. Support price tiers by context length (some providers charge more above a context threshold).
- **Currency.** Several of the providers in R4 bill in CNY. Store the native price and currency, and convert to the budget currency with a rate from the same file (with its own `asOf`). Budgets stay in one currency per run.
- **Unknown models are an explicit setting,** not a hard-coded rule. The options are `dearest` (Part II behaviour), `error` (refuse to launch), or a named fallback entry. When the launcher starts with a model that has no price, the result block says so. With cheap non-Claude models, "price as the dearest Claude model" would trigger the wrap-up after a small fraction of the real budget.
- **Updating.** Document how to refresh the file. Optionally add a `lean-worker prices check` command that prints each entry's `asOf` and flags old ones. It must not scrape prices at run time.
- **One source of truth.** The launcher's own `cost:` line and the hook must price with the same file (see R4: for non-Claude models Claude Code's own `total_cost_usd` is wrong).

*Done when:* a new model is added or a price changed by editing the user price file only, with no rebuild, and both the hook and the `cost:` line pick it up.

### R4. Non-Claude models and subscriptions

We don't run only Claude models. In particular we use GLM (Zhipu / Z.ai), MiniMax, DeepSeek and Qwen (Alibaba), often through their Anthropic-compatible endpoints (`ANTHROPIC_BASE_URL` + a key) in Claude Code, or natively in opencode (R5). Some are paid per token, some through a subscription / coding plan, and the Claude login may itself be a subscription.

- **Billing mode per account, not per model.** Each price-file entry (or a provider block above it) says how usage is paid: `metered` (money is spent), or `subscription` / `plan` (usage counts against a quota; the list-price figure is an equivalent, not a charge). A run can mix them: an orchestrator on a Claude subscription starting workers on a metered DeepSeek key, or a GLM coding plan.
- **What the budget means for each mode (D3).** For metered, the budget is money. For subscription and coding plans, the budget and the wrap-up threshold use the list-price equivalent from the price file (upstream README already treats Claude subscriptions this way). Plan-unit limits (requests or prompts) are out of scope. The result block and `runs.jsonl` must say which mode applied (`cost: $0.12 metered` vs `cost: ≈$0.12 list-equivalent, subscription`).
- **Provider-specific usage fields.** Not every Anthropic-compatible endpoint fills `cache_creation` / `cache_read_input_tokens` the way Anthropic does. Some report cache hits differently, and some report no cache split. The usage parser must map these per provider and must never throw on a missing field. Where cache writes cost the same as input (common for these providers), the price file says so instead of assuming ×1.25 / ×2.
- **Claude Code's own cost is wrong for non-Claude models.** When a worker runs a GLM/MiniMax/DeepSeek/Qwen model through Claude Code, `total_cost_usd` in `stream-json` is computed by Claude Code, which doesn't know these models. The launcher must recompute cost from usage with the price file and show both only if they differ meaningfully.
- **Hard cap.** `--max-budget-usd` is enforced by Claude Code with the same wrong pricing, so for non-Claude models it can't be trusted as the hard cap. Either the launcher enforces the hard cap itself (kill the worker when recomputed spend passes the budget), or the hook's second threshold at 100% does it. Decide in R0.
- **Model ids.** Profiles need `provider/model` ids (or a provider field) plus the endpoint and credential source per provider. Credentials never go into `profiles.json`; reference an env var name or a settings file.

*Done when:* a worker on at least one Chinese model (metered) and one on a coding-plan subscription both wrap up at the configured share, and their `cost:` lines match a hand calculation from the price file within a few percent.

### R5. opencode as a second runtime

The repo is built around Claude Code (`claude -p`, `--settings`, PreToolUse hooks, `~/.claude/projects/*.jsonl` transcripts). We also run sessions in **opencode**, both headless (`opencode run ...`) and manually (the TUI). The feature has to cover both runtimes **in the first version (D4)**, so the launcher needs a runtime abstraction. Both runtimes read the same `.lean-worker/` state (D2).

- **Runtime selection.** A profile key `runtime: "claude" | "opencode"` (default `claude`) and a `--runtime` option. Everything runtime-specific sits behind it: how the worker is started, how minimal context is enforced, where usage is read from, how tools are blocked, and how a handoff is requested.
- **Headless opencode worker.** Map the lean profile onto opencode: a per-run config with no instructions files (AGENTS.md), no MCP servers, a fixed tool/permission set, the chosen `provider/model`, and JSON output for the launcher to parse. Check against the current opencode docs which flags and config keys exist. Don't assume they match Claude's.
- **Wrap-up in opencode.** opencode has no Claude-style `settings.json` hooks. The equivalent is a plugin hook before tool execution (a per-run plugin the launcher injects, like the injected Claude hook). It needs a way to read the session's accumulated token usage. Find out and write down where opencode stores session/message usage (its storage directory or database), and whether a plugin receives usage directly. If opencode can't block tools before a call, the fallback is launcher-side: watch the JSON stream, and past the threshold stop the run and start a short handoff-only follow-up.
- **Continuation is runtime-neutral.** `--continue-from` must work across runtimes: `task.md` + `report.md` are plain text, so a run wrapped up in Claude Code can continue in opencode and the other way round (for example to move from an expensive model to a cheaper one).
- **Manual (TUI) sessions.** The launcher doesn't start these, but the cost tooling should read them. Provide a cost reader that, given a session id (Claude transcript or opencode session), prices it with the R3 price file and the R4 billing mode. This replaces Part II's out-of-repo `session-cost.js` and is what acceptance test 5 runs.
- **Orchestrator side.** SKILL.md is Claude-Code-specific (skill loading, background Bash, completion notifications). Say how an opencode orchestrator uses the launcher, and keep the result block as the contract between orchestrator and worker in both.

*Done when:* the R7 wrap-up and continuation tests pass with `runtime: opencode` headless, and the cost reader prices one manual opencode session and one manual Claude Code session.

### R6. Reuse from the debrief repo

The photos-to-document work was already done for the debrief repo (`~/Repository/debrief`, raw material in `~/Repository/debrief-raw`). Reuse from it:

- the transcription method: per-photo chunks, stitching, `[?]` fidelity marks, nothing invented (debrief ADR-0003, foundation "Fidelity marks"). Part II above follows it.
- the cross-platform rule for OS-bound tooling (debrief foundation, "Historical-evidence caveat"; used in R1).
- the "reproduce under the current constitution" decision. If `claude-lean-worker` is brought under the Legislator layout, record the differences between Part II and the result in an ADR, as debrief ADR-0003 did.

### R7. Acceptance tests (replaces Part II §9)

Keep Part II's five tests. Parametrize them by runtime (`claude`, `opencode`) and by one Claude and one non-Claude model, and add:

6. **Price file change takes effect:** raise a model's price in the user price file, and the same task wraps up earlier with no rebuild.
7. **Unknown model policy:** a model missing from the price file gives the configured behaviour (`dearest` / `error` / fallback), and the result block names the model.
8. **Continuation of a continuation:** make it work, or reject it with a clear message. Part II §10 left it untested.
9. **Cross-runtime continuation:** wrap up in one runtime and continue in the other.
10. **Linux paths:** a runs root and skill dir under a home path containing a space.

Tests must be scriptable (a `tests/acceptance.sh`, or a launcher `--self-test`), cheap (the smallest model, tiny budgets) and must record their cost in `runs.jsonl` like any run.

---

## Part IV: Review findings and open questions

Found while transcribing and checking Part II against upstream `13c9fa3`. They go into the R0 review.

**F1. Wrap-up is tied to keeping all user hooks.** Upstream deliberately turns off user/project/plugin hooks in lean mode (`disableAllHooks`) so personal integrations don't leak into workers. Part II can only run its injected hook with `keepHooks: true`, so turning wrap-up on quietly brings back every hook the user has. Look for a way to run the wrap-up hook while user hooks stay off. If there is none, say so in the result block and README, and keep a launcher-side watcher over `stream.jsonl` as the hooks-off path (Part II rejected this, but only to avoid the transcript dependency).

**F2. Bare mode gets no wrap-up.** With an API key (bare mode) hooks are skipped, so wrap-up is silently off. The same launcher-side watcher from F1 would cover bare mode too.

**F3. The session-persistence trade-off.** Turning persistence on leaves worker sessions in the user's session history (`claude --resume` lists them, and they count in transcript-based tooling). That was intended on the other PC. Make it visible and optional here.

**F4. `--claude-settings` flips auto mode to bare** (upstream line 94: `hasKey = ... || o.ClaudeSettings is not null`). That makes wrap-up silently off for anyone who passes a settings file. Fix it in the launcher instead of documenting "add `--mode lean`".

**F5. Hook fails open.** A broken hook (bad JSON, missing node, missing price) allows every call, and then only `--max-budget-usd` protects the run. For non-Claude models that cap is itself unreliable (R4). The result block should report whether the hook ran at all, for example a heartbeat or last-check field written to the run dir.

**F6. Two fragments are unreadable** (Part II §4: the value after `thresholdUsd:` and after `permissionDecisionReason`). They are almost certainly `threshold` and `: reason`. Confirm against the backup on the other PC if it can be reached.

**F7. The handoff is only as good as the model's last turn.** Part II §10 notes the 20% margin may not be enough at large contexts. Consider sizing the margin from the current context size and output price instead of a fixed share, or a minimum absolute reserve.

**F8. Upstream template model ids are behind** (`claude-sonnet-5` vs `claude-sonnet-5-5` in Part II §7). With prices as configuration (R3), model ids in templates and prices should be updated together.

**Open questions:** none left. The operator answered them on 2026-09-29; see decisions D1-D5 at the top.
