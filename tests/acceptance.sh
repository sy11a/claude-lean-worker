#!/usr/bin/env bash
# Acceptance tests for the launcher: real workers on small models with tiny budgets (a few cents of list price
# in total). Needs claude (logged in), jq, and for the opencode and GLM cases opencode with a zai-coding-plan login.
#
# Usage: tests/acceptance.sh [--claude-only] [--keep]
#   --claude-only  skip the cases that need opencode or a z.ai GLM Coding Plan
#   --keep         keep the scratch directory for inspection
set -uo pipefail

claude_only=0 keep=0
for a in "$@"; do case "$a" in --claude-only) claude_only=1 ;; --keep) keep=1 ;; *) echo "unknown option $a"; exit 2 ;; esac; done

repo="$(cd "$(dirname "$0")/.." && pwd)"
launcher_dir="$repo/skills/lean-worker/launcher"
dotnet build "$launcher_dir" -c Release -v q -nologo >/dev/null || { echo "launcher build failed"; exit 2; }
L="$launcher_dir/bin/Release/net8.0/LeanWorker"
[ -x "$L" ] || L="dotnet $launcher_dir/bin/Release/net8.0/LeanWorker.dll"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/lean-worker acceptance XXXXXX")"   # a space on purpose (R7 case 10)
cwd="$scratch/cwd" rr="$scratch/runs root"
mkdir -p "$cwd" "$rr/inbox/glob12" "$rr/inbox/count"
for x in a b c d e f g h i j k l; do touch "$cwd/${x}1.txt"; done
printf 'Call the Glob tool 12 times, one call per turn, with the patterns a* b* c* d* e* f* g* h* i* j* k* l* in that order. Then reply DONE and the number of Glob calls you made.\n' > "$rr/inbox/glob12/task.md"
printf 'Use Glob once with pattern *.txt and reply with the number of files only.\n' > "$rr/inbox/count/task.md"
glob12="$rr/inbox/glob12/task.md" count="$rr/inbox/count/task.md"
haiku=(--model claude-haiku-4-5 --effort low --no-project-notes --tools Glob)
glm=(--model zai-coding-plan/glm-5.3 --no-project-notes --tools Glob)

pass=0 fail=0
check() { if eval "$2"; then pass=$((pass + 1)); echo "  ok    $1"; else fail=$((fail + 1)); echo "  FAIL  $1"; fi; }
run() { (cd "$cwd" && $L --runs-root "$rr" "$@") > "$scratch/out.txt" 2>&1; code=$?; last="$(ls -d "$rr"/runs/* 2>/dev/null | tail -1)"; }
cmd() { (cd "$cwd" && $L "$1" --runs-root "$rr" "${@:2}") > "$scratch/out.txt" 2>&1; code=$?; }
field() { jq -r "$1" "$last/summary.json"; }
patterns() { jq -r 'select(.type=="assistant") | .message.content[]? | select(.type=="tool_use") | .input.pattern' "$last/stream.jsonl" | sort -u | tr -d '*\n'; }

echo "== 1. wrap-up triggers (claude, haiku)"
run --task "$glob12" "${haiku[@]}" --max-budget-usd 0.03 --wrap-up-at 0.3
run1="$last"
check "exit 3 and status wrapped-up" '[ $code = 3 ] && [ "$(field .status)" = wrapped-up ]'
check "wrapup.json written, report starts with HANDOFF" '[ -f "$last/wrapup.json" ] && head -1 "$last/report.md" | grep -q HANDOFF'
check "hook ran (hook.log) and user settings stayed out" '[ -s "$last/hook.log" ] && grep -q -- "--setting-sources \"\"" "$last/command.txt"'
check "metered cost within 5% of Claude Code's own" 'awk -v a="$(field .total_cost_usd)" -v b="$(field .reported_cost_usd)" "BEGIN{d=a-b; if(d<0)d=-d; exit !(d <= b*0.05)}"'

echo "== 2. continuation"
# The last tool call of a wrapped-up run is the one the hook denied; only the ones before it were done.
done1="$(jq -r 'select(.type=="assistant") | .message.content[]? | select(.type=="tool_use") | .input.pattern' "$run1/stream.jsonl" | sed '$d' | sort -u | tr -d '*\n')"
run --continue-from "$run1" "${haiku[@]}" --max-budget-usd 0.05
check "success" '[ $code = 0 ] && [ "$(field .status)" = success ]'
check "only the remaining patterns were called (first run: $done1)" '[ -z "$(comm -12 <(echo "$done1" | fold -w1 | sort) <(patterns | fold -w1 | sort))" ]'
check "task.md = original + one continuation heading" '[ "$(grep -c "^## Continuation (lean-worker)" "$last/task.md")" = 1 ] && head -1 "$last/task.md" | grep -q "Call the Glob tool"'
run2="$last"

echo "== 3. keep-hooks loads the user's and project's hooks; the default does not"
mkdir -p "$cwd/.claude"
printf '{"hooks":{"PreToolUse":[{"matcher":".*","hooks":[{"type":"command","command":"touch \\"%s/project-hook-ran\\""}]}]}}\n' "$scratch" > "$cwd/.claude/settings.json"
run --task "$count" "${haiku[@]}" --max-budget-usd 0.03
check "default: project hook did not run" '[ ! -e "$scratch/project-hook-ran" ]'
run --task "$count" "${haiku[@]}" --max-budget-usd 0.03 --keep-hooks
check "--keep-hooks: project hook ran" '[ -e "$scratch/project-hook-ran" ]'
rm -rf "$cwd/.claude"

echo "== 4. wrap-up off"
run --task "$count" "${haiku[@]}" --max-budget-usd 0.03 --wrap-up-at 0
check "no hook injected, wrap-up off in the result" '[ "$(jq ".hooks // null" "$last/settings.json")" = null ] && grep -q "wrap-up off" "$scratch/out.txt"'

echo "== 6. a price change takes effect without a rebuild"
printf '{"models":{"anthropic/claude-haiku-4-5":{"input":100,"output":500,"cacheRead":10,"cacheWrite":125}}}\n' > "$scratch/dear.json"
run --task "$count" "${haiku[@]}" --max-budget-usd 0.5 --prices "$scratch/dear.json"
check "100x price -> wrapped up on a task that normally costs under 1 cent" '[ "$(field .status)" = wrapped-up ] || [ "$(field .status)" = budget-exceeded ]'

echo "== 7. unknown-model policy"
printf '{"unknownModel":"error"}\n' > "$scratch/strict.json"
run --task "$count" --model claude-nonexistent-9 --no-project-notes --prices "$scratch/strict.json"
check "unknownModel=error refuses to launch (exit 2) and names the model" '[ $code = 2 ] && grep -q "claude-nonexistent-9" "$scratch/out.txt"'

echo "== 8. continuation of a continuation"
run --continue-from "$run2" "${haiku[@]}" --max-budget-usd 0.03
check "still exactly one continuation heading" '[ "$(grep -c "^## Continuation (lean-worker)" "$last/task.md")" = 1 ]'

echo "== quota command and chain fallback"
if [ $claude_only = 0 ]; then
    cmd quota; check "quota prints the z.ai windows" 'grep -q "zai-coding-plan" "$scratch/out.txt" && grep -q weekly "$scratch/out.txt"'
    printf '{"providers":{"zai-coding-plan":{"quota":{"maxPercent":{"5h":0}}}}}\n' > "$scratch/full.json"
    run --task "$count" --model zai-coding-plan/glm-5.3 --no-project-notes --tools Glob --prices "$scratch/full.json" --max-budget-usd 0.03
    check "a single-model chain over quota still runs, with a note" 'grep -q "over its quota threshold" "$scratch/out.txt"'
fi

if [ $claude_only = 0 ]; then
    echo "== 1g. wrap-up on GLM through z.ai's Anthropic endpoint (claude runtime)"
    run --task "$glob12" "${glm[@]}" --max-budget-usd 0.01 --wrap-up-at 0.3
    glmrun="$last"
    check "wrapped-up, input tokens metered (z.ai reports them in message_delta)" '[ "$(field .status)" = wrapped-up ] && [ "$(field .tokens.input)" -gt 0 ]'

    echo "== 1o. wrap-up in the opencode runtime"
    run --runtime opencode --task "$glob12" "${glm[@]}" --max-budget-usd 0.02 --wrap-up-at 0.3
    check "wrapped-up with a HANDOFF report" '[ $code = 3 ] && grep -q HANDOFF "$last/report.md"'
    check "the provider block is not recorded" '! grep -q "apiKey" "$last/opencode-config.json"'

    echo "== 9. cross-runtime continuation (claude/GLM -> opencode/GLM)"
    run --continue-from "$glmrun" --runtime opencode "${glm[@]}" --max-budget-usd 0.05
    check "success in opencode, continued_from recorded" '[ "$(field .status)" = success ] && [ "$(field .runtime)" = opencode ] && [ "$(field .continued_from)" != null ]'
fi

echo "== 5. cost of a manual session"
cmd cost --claude "$run1/stream.jsonl"
check "cost reads a Claude stream/transcript" 'grep -q "at list price" "$scratch/out.txt"'

echo
echo "passed $pass, failed $fail  (runs: $rr)"
(cd "$cwd" && $L stats --runs-root "$rr")
[ $keep = 1 ] || [ $fail -gt 0 ] || rm -rf "$scratch"
[ $fail = 0 ]
