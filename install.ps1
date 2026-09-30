<#
.SYNOPSIS
    Installs the lean-worker skill for orchestrating sessions in Claude Code and/or opencode. Runs without any agent.

.DESCRIPTION
    1. Checks prerequisites: .NET SDK 8+, claude (with --bare) when Claude Code orchestrates, opencode when
       opencode orchestrates, and an API key (optional).
    2. Copies skills/lean-worker into each orchestrator's skill folder: ~/.claude/skills and ~/.config/opencode/skills
       (default), or a project's .claude/skills and .opencode/skills (-Scope Project). -Orchestrator Claude,
       Opencode or Both; the default is Both when opencode is on PATH, else Claude.
    3. Builds the launcher once, so the first worker run starts immediately.
    4. With -ProjectPath, prepares that project:
         - .lean-worker/project.md and profiles.json from the templates (existing files are never overwritten)
         - .gitignore entries for .lean-worker/runs/ and .lean-worker/inbox/
         - an allow rule so the orchestrator can start workers without a permission prompt: in
           <project>/.claude/settings.json (Claude Code) and <project>/opencode.json (opencode). The files are
           backed up first; use -SkipPermission to skip this step.
         - with -AddRule, the delegation rule (templates/orchestrator-rule.md) appended once to the instruction
           file each orchestrator reads: CLAUDE.md (Claude Code), AGENTS.md (opencode).
    5. With -SmokeTest, runs one tiny read-only worker (Haiku, budget $0.10) and prints its result block.

    Safe to re-run: it updates the skill and leaves your project files alone, including
    .lean-worker/prices.json and ~/.config/lean-worker/prices.json. On Linux/macOS, install.sh does the same.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -ProjectPath C:\src\my-product -SmokeTest
#>
[CmdletBinding()]
param(
    [string] $ProjectPath,
    [ValidateSet('User', 'Project')] [string] $Scope = 'User',
    [ValidateSet('Claude', 'Opencode', 'Both')] [string] $Orchestrator,
    [switch] $SkipPermission,
    [switch] $AddRule,
    [switch] $SmokeTest
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Step([string] $text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Ok([string] $text) { Write-Host "   ok    $text" -ForegroundColor Green }
function Warn([string] $text) { Write-Host "   WARN  $text" -ForegroundColor Yellow }
function Die([string] $text) { Write-Host "   FAIL  $text" -ForegroundColor Red; exit 1 }

$repoRoot = $PSScriptRoot
$source = Join-Path $repoRoot 'skills/lean-worker'
if (-not (Test-Path -LiteralPath (Join-Path $source 'SKILL.md'))) { Die "run this script from the downloaded repository (skills\lean-worker not found next to it)" }
if ($Scope -eq 'Project' -and -not $ProjectPath) { Die "-Scope Project needs -ProjectPath" }
if ($AddRule -and -not $ProjectPath) { Die "-AddRule needs -ProjectPath" }
$opencode = Get-Command opencode -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Orchestrator) { if ($opencode) { $Orchestrator = 'Both' } else { $Orchestrator = 'Claude' } }
function Uses([string] $o) { return $Orchestrator -eq 'Both' -or $Orchestrator -eq $o }
if ($ProjectPath) {
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Container)) { Die "project path not found: $ProjectPath" }
    $ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
}

# ---------- 1. prerequisites ----------
Step 'Checking prerequisites'
Ok "orchestrator: $Orchestrator"
$claude = Get-Command claude -ErrorAction SilentlyContinue | Select-Object -First 1
if ($claude) {
    $help = (& $claude.Source --help 2>&1 | Out-String)
    if ($help -notmatch '--bare') { Die "this Claude Code version has no --bare; update Claude Code" }
    Ok ("claude: " + (& $claude.Source --version 2>&1 | Out-String).Trim())
} elseif (Uses 'Claude') { Die "'claude' (Claude Code) is not on PATH" }
else { Ok "no claude on PATH: workers can use only the opencode runtime" }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) { Die "'dotnet' is not on PATH; install the .NET SDK 8 or newer" }
$sdks = @(& $dotnet.Source --list-sdks 2>$null)
$okSdk = $sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 }
if (-not $okSdk) { Die ".NET SDK 8 or newer not found (dotnet --list-sdks: $($sdks -join '; '))" }
Ok ("dotnet SDK: " + (($okSdk | ForEach-Object { ($_ -split ' ')[0] }) -join ', '))

if ($opencode) { Ok ("opencode: " + (& $opencode.Source --version 2>&1 | Out-String).Trim() + " (runtime ""opencode"" available)") }
elseif (Uses 'Opencode') { Die "'opencode' is not on PATH" }
else { Ok "no opencode on PATH: only the claude runtime is available (optional)" }

if ([string]::IsNullOrEmpty($env:ANTHROPIC_API_KEY)) {
    Ok "no ANTHROPIC_API_KEY: workers will run in lean mode on your Claude Code login (Pro/Max/Team/Enterprise subscription). Make sure 'claude' is logged in."
} else { Ok "ANTHROPIC_API_KEY is set: workers use the key (lean mode with wrap-up; bare mode when wrap-up is off)" }

# ---------- 2. copy the skill, 3. build the launcher ----------
$userHome = $env:USERPROFILE
if (-not $userHome) { $userHome = $HOME }
$opencodeConfig = if ($env:XDG_CONFIG_HOME) { Join-Path $env:XDG_CONFIG_HOME 'opencode' } else { Join-Path $userHome '.config/opencode' }
$targets = @()
if (Uses 'Claude') { $targets += if ($Scope -eq 'User') { Join-Path $userHome '.claude/skills/lean-worker' } else { Join-Path $ProjectPath '.claude/skills/lean-worker' } }
if (Uses 'Opencode') { $targets += if ($Scope -eq 'User') { Join-Path $opencodeConfig 'skills/lean-worker' } else { Join-Path $ProjectPath '.opencode/skills/lean-worker' } }
foreach ($target in $targets) {
    Step "Installing the skill at $target"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    # Replace the skill's own files but keep a previous build (bin/obj) to save time.
    foreach ($item in @('SKILL.md', 'models.md', 'templates')) {
        $from = Join-Path $source $item
        $to = Join-Path $target $item
        if (Test-Path -LiteralPath $from -PathType Container) {
            if (Test-Path -LiteralPath $to) { Remove-Item -LiteralPath $to -Recurse -Force }
            Copy-Item -LiteralPath $from -Destination $to -Recurse
        } else { Copy-Item -LiteralPath $from -Destination $to -Force }
    }
    # The launcher's sources, price book and opencode plugin: every top-level file, replacing the old ones.
    $launcherTarget = Join-Path $target 'launcher'
    New-Item -ItemType Directory -Force -Path $launcherTarget | Out-Null
    Get-ChildItem -LiteralPath $launcherTarget -File | Remove-Item -Force
    Get-ChildItem -LiteralPath (Join-Path $source 'launcher') -File | Copy-Item -Destination $launcherTarget -Force
    Ok "skill installed"
    $buildOut = & $dotnet.Source build $launcherTarget -c Release -v q -nologo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $buildOut; Die "launcher build failed" }
    Ok "launcher built"
}
$launcher = Join-Path $targets[0] 'launcher'
$launcherFwd = $launcher -replace '\\', '/'
$runCmd = "dotnet run --project `"$launcherFwd`" -c Release --"

# Appends the delegation rule to an instruction file once; a symlinked file already covered is skipped.
$ruleDone = @()
function Add-RuleTo([string] $file) {
    $real = $file
    $item = Get-Item -LiteralPath $file -ErrorAction SilentlyContinue
    if ($item -and $item.LinkType -eq 'SymbolicLink') {
        $t = [string]($item.Target | Select-Object -First 1)
        $real = if ([IO.Path]::IsPathRooted($t)) { $t } else { Join-Path (Split-Path -Parent $file) $t }
    }
    $real = [IO.Path]::GetFullPath($real)
    $name = Split-Path -Leaf $file
    if ($script:ruleDone -contains $real) { Ok "${name}: the rule is already in $(Split-Path -Leaf $real)"; return }
    $script:ruleDone += $real
    if ((Test-Path -LiteralPath $real) -and (Select-String -LiteralPath $real -SimpleMatch 'lean-worker:orchestrator-rule' -Quiet)) { Ok "${name}: delegation rule already present"; return }
    $prefix = ''
    if ((Test-Path -LiteralPath $real) -and ((Get-Item -LiteralPath $real).Length -gt 0)) { $prefix = [Environment]::NewLine }
    [IO.File]::AppendAllText($real, $prefix + [IO.File]::ReadAllText((Join-Path $source 'templates/orchestrator-rule.md'), $utf8), $utf8)
    Ok "${name}: delegation rule added"
}

# ---------- 4. prepare the project ----------
if ($ProjectPath) {
    Step "Preparing project $ProjectPath"
    $lw = Join-Path $ProjectPath '.lean-worker'
    New-Item -ItemType Directory -Force -Path (Join-Path $lw 'inbox') | Out-Null
    foreach ($f in @('project.md', 'profiles.json')) {
        $dst = Join-Path $lw $f
        if (Test-Path -LiteralPath $dst) { Ok ".lean-worker\$f exists, left unchanged" }
        else { Copy-Item -LiteralPath (Join-Path $source "templates/$f") -Destination $dst; Ok ".lean-worker\$f created from template (fill it in)" }
    }

    $gi = Join-Path $ProjectPath '.gitignore'
    $existing = @()
    if (Test-Path -LiteralPath $gi) { $existing = @(Get-Content -LiteralPath $gi) }
    $add = @('.lean-worker/runs/', '.lean-worker/inbox/', '.lean-worker/runs.jsonl') | Where-Object { $existing -notcontains $_ }
    if ($add.Count -gt 0) {
        $prefix = ''
        if (Test-Path -LiteralPath $gi) {
            $giRaw = [IO.File]::ReadAllText($gi)
            if ($giRaw.Length -gt 0 -and -not $giRaw.EndsWith("`n")) { $prefix = [Environment]::NewLine }
        }
        [IO.File]::AppendAllText($gi, $prefix + ($add -join [Environment]::NewLine) + [Environment]::NewLine, $utf8)
        Ok (".gitignore: added " + ($add -join ', '))
    } else { Ok ".gitignore already covers .lean-worker" }

    if (-not $SkipPermission -and (Uses 'Claude')) {
        $rule = 'Bash(dotnet run --project:*)'
        $claudeDir = Join-Path $ProjectPath '.claude'
        $settings = Join-Path $claudeDir 'settings.json'
        New-Item -ItemType Directory -Force -Path $claudeDir | Out-Null
        if (Test-Path -LiteralPath $settings) {
            $raw = [IO.File]::ReadAllText($settings, $utf8)
            try { $json = $raw | ConvertFrom-Json } catch { Die ".claude\settings.json is not valid JSON; fix it or use -SkipPermission" }
            if ($null -eq $json) { $json = New-Object PSObject }
        } else { $raw = $null; $json = New-Object PSObject }
        if (-not $json.PSObject.Properties['permissions']) { $json | Add-Member -NotePropertyName permissions -NotePropertyValue (New-Object PSObject) }
        if (-not $json.permissions.PSObject.Properties['allow']) { $json.permissions | Add-Member -NotePropertyName allow -NotePropertyValue @() }
        $allow = @($json.permissions.allow)
        if ($allow -contains $rule) { Ok "permission rule already present: $rule" }
        else {
            if ($raw) {
                $backup = "$settings.bak-" + (Get-Date).ToString('yyyyMMdd-HHmmss')
                [IO.File]::WriteAllText($backup, $raw, $utf8)
                Ok "backed up settings to $backup"
            }
            $json.permissions.allow = @($allow + $rule)
            [IO.File]::WriteAllText($settings, ($json | ConvertTo-Json -Depth 32), $utf8)
            Ok "added permission rule to .claude\settings.json: $rule"
        }
    }

    if (-not $SkipPermission -and (Uses 'Opencode')) {
        $pattern = 'dotnet run --project*'
        $oc = Join-Path $ProjectPath 'opencode.json'
        if (-not (Test-Path -LiteralPath $oc) -and (Test-Path -LiteralPath (Join-Path $ProjectPath 'opencode.jsonc'))) {
            Warn "the project has opencode.jsonc: add ""permission"": {""bash"": {""$pattern"": ""allow""}} to it yourself"
        } else {
            if (Test-Path -LiteralPath $oc) {
                $raw = [IO.File]::ReadAllText($oc, $utf8)
                try { $json = $raw | ConvertFrom-Json } catch { Die "opencode.json is not valid JSON; fix it or use -SkipPermission" }
            } else { $raw = $null; $json = [PSCustomObject]@{ '$schema' = 'https://opencode.ai/config.json' } }
            if (-not $json.PSObject.Properties['permission']) { $json | Add-Member -NotePropertyName permission -NotePropertyValue (New-Object PSObject) }
            $bash = $json.permission.PSObject.Properties['bash']
            if ($bash -and $bash.Value -is [string] -and $bash.Value -eq 'allow' -or ($bash -and $bash.Value -isnot [string] -and $bash.Value.PSObject.Properties[$pattern] -and $bash.Value.$pattern -eq 'allow')) {
                Ok "opencode permission already present: $pattern"
            } else {
                if ($raw) {
                    $backup = "$oc.bak-" + (Get-Date).ToString('yyyyMMdd-HHmmss')
                    [IO.File]::WriteAllText($backup, $raw, $utf8)
                    Ok "backed up opencode.json to $backup"
                }
                # A plain string (e.g. "ask") becomes the catch-all of an object, so its meaning is kept.
                if (-not $bash) { $obj = New-Object PSObject }
                elseif ($bash.Value -is [string]) { $obj = [PSCustomObject]@{ '*' = $bash.Value } }
                else { $obj = $bash.Value }
                $obj | Add-Member -NotePropertyName $pattern -NotePropertyValue 'allow' -Force
                $json.permission | Add-Member -NotePropertyName bash -NotePropertyValue $obj -Force
                [IO.File]::WriteAllText($oc, ($json | ConvertTo-Json -Depth 32), $utf8)
                Ok "added opencode permission to opencode.json: $pattern"
            }
        }
    }

    if ($AddRule) {
        if (Uses 'Claude') { Add-RuleTo (Join-Path $ProjectPath 'CLAUDE.md') }
        if (Uses 'Opencode') { Add-RuleTo (Join-Path $ProjectPath 'AGENTS.md') }
    }
}

# ---------- 5. smoke test ----------
if ($SmokeTest -and -not $claude) { Warn "smoke test skipped: it runs on Claude Haiku and needs claude"; $SmokeTest = $false }
if ($SmokeTest) {
    Step 'Smoke test (Haiku, read-only, budget $0.10)'
    $work = $ProjectPath
    if (-not $work) { $work = Join-Path ([IO.Path]::GetTempPath()) ("lean-worker-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory -Path $work | Out-Null }
    $taskDir = Join-Path $work '.lean-worker/inbox/smoke-test'
    New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
    $task = Join-Path $taskDir 'task.md'
    [IO.File]::WriteAllText($task, "# Task: smoke test`n`nDo not read or change any file. Reply with exactly one line: lean-worker smoke test OK`n", $utf8)
    Push-Location $work
    try {
        & $dotnet.Source run --project $launcher -c Release -- --task $task --model claude-haiku-4-5 --effort low --tools Read --max-budget-usd 0.1 --no-project-notes
        $smokeExit = $LASTEXITCODE
    } finally { Pop-Location }
    if ($smokeExit -eq 0) { Ok "smoke test passed" } else { Warn "smoke test did not succeed (exit $smokeExit); read the block above" }
}

# ---------- next steps ----------
Step 'Done'
Write-Host "   Launcher command (the skill uses it for you):"
Write-Host "     $runCmd --task .lean-worker/inbox/<name>/task.md --profile code"
Write-Host ""
Write-Host "   Next:"
Write-Host "   1. Start a new Claude Code / opencode session so it sees the skill."
if ($ProjectPath) {
    Write-Host "   2. In a session in $ProjectPath, ask:"
    Write-Host "        /lean-worker set up .lean-worker/project.md and profiles.json for this repository"
    Write-Host "      then review and edit both files by hand."
    Write-Host "   3. Delegate a task:  /lean-worker <what to do>; done when <command> passes"
} else {
    Write-Host "   2. Re-run with -ProjectPath <your repo> to prepare a project (templates, .gitignore, permission)."
}
