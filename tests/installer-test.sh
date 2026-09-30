#!/usr/bin/env bash
# Installer tests in a sandbox home: both orchestrators, project scope and user scope, re-run idempotence.
# No model is called.
# Usage: tests/installer-test.sh [--keep]
set -uo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
sb="$(mktemp -d "${TMPDIR:-/tmp}/lean-worker installer XXXXXX")"
pass=0 fail=0
check() { if eval "$2"; then pass=$((pass + 1)); echo "  ok    $1"; else fail=$((fail + 1)); echo "  FAIL  $1"; fi; }

run_case() { # $1 = installer function, $2 = label
    local inst="$1" label="$2" h="$sb/$2/home" p="$sb/$2/project" out="$sb/$2"
    mkdir -p "$h" "$p" && ln -s AGENTS.md "$p/CLAUDE.md" && printf '# Project\n' > "$p/AGENTS.md"
    printf '{"permission": {"bash": "ask"}}\n' > "$p/opencode.json"
    local nuget="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
    sandboxed() { (export HOME="$h" DOTNET_CLI_HOME="$h" NUGET_PACKAGES="$nuget"; unset XDG_CONFIG_HOME; "$inst" "$@"); }
    echo "== $label: user scope, both orchestrators, --add-rule"
    sandboxed both > "$out/out1.txt" 2>&1; local c1=$?
    check "exit 0" '[ $c1 = 0 ] || { tail -20 "$out/out1.txt"; false; }'
    check "skill in Claude Code's and opencode's skill directories, launcher built" '[ -f "$h/.claude/skills/lean-worker/SKILL.md" ] && [ -f "$h/.config/opencode/skills/lean-worker/SKILL.md" ] && ls "$h/.config/opencode/skills/lean-worker/launcher/bin/Release/"*/LeanWorker* >/dev/null 2>&1'
    check "Claude Code allow rule" 'jq -e ".permissions.allow | index(\"Bash(dotnet run --project:*)\")" "$p/.claude/settings.json" >/dev/null'
    check "opencode permission added, the old catch-all kept" 'jq -e ".permission.bash[\"dotnet run --project*\"] == \"allow\" and .permission.bash[\"*\"] == \"ask\"" "$p/opencode.json" >/dev/null'
    check "the rule is in AGENTS.md once (CLAUDE.md is a symlink to it)" '[ "$(grep -c "lean-worker:orchestrator-rule" "$p/AGENTS.md")" = 1 ] && [ -L "$p/CLAUDE.md" ]'
    sandboxed both > "$out/out2.txt" 2>&1
    check "a re-run duplicates nothing" '[ "$(grep -c "lean-worker:orchestrator-rule" "$p/AGENTS.md")" = 1 ] && [ "$(jq ".permissions.allow | length" "$p/.claude/settings.json")" = 1 ] && [ "$(jq ".permission.bash | length" "$p/opencode.json")" = 2 ]'
    local p2="$out/project2"; mkdir -p "$p2"
    echo "== $label: project scope, opencode only"
    sandboxed opencode-project "$p2" > "$out/out3.txt" 2>&1; local c3=$?
    check "exit 0, skill in <project>/.opencode/skills only, rule in a new AGENTS.md" '[ $c3 = 0 ] && [ -f "$p2/.opencode/skills/lean-worker/SKILL.md" ] && [ ! -e "$p2/.claude/skills" ] && grep -q "lean-worker:orchestrator-rule" "$p2/AGENTS.md" && [ ! -e "$p2/CLAUDE.md" ]'
    check "opencode only: opencode.json permission, no .claude/settings.json" 'jq -e ".permission.bash[\"dotnet run --project*\"] == \"allow\"" "$p2/opencode.json" >/dev/null && [ ! -e "$p2/.claude/settings.json" ]'
}

sh_installer() { # $1 = mode, $2 = project for the project-scope mode
    case "$1" in
        both) "$repo/install.sh" --project "$p" --orchestrator both --add-rule ;;
        opencode-project) "$repo/install.sh" --project "$2" --scope project --orchestrator opencode --add-rule ;;
    esac
}
p="$sb/sh/project"; run_case sh_installer sh

echo; echo "passed $pass, failed $fail  (sandbox: $sb)"
[ "${1:-}" = --keep ] || [ $fail -gt 0 ] || rm -rf "$sb"
[ $fail = 0 ]
