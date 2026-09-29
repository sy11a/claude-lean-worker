<#
.SYNOPSIS
    Installs the lean-worker skill for Claude Code. Runs without any agent.

.DESCRIPTION
    1. Checks prerequisites: claude (with --bare), .NET SDK 8+, and an API key.
    2. Copies skills/lean-worker to the user's skills folder (default) or to a project's .claude/skills.
    3. Builds the launcher once, so the first worker run starts immediately.
    4. With -ProjectPath, prepares that project:
         - .lean-worker/project.md and profiles.json from the templates (existing files are never overwritten)
         - .gitignore entries for .lean-worker/runs/ and .lean-worker/inbox/
         - an allow rule in <project>/.claude/settings.json so the orchestrator can start workers
           without a permission prompt. The file is backed up first; use -SkipPermission to skip this step.
    5. With -SmokeTest, runs one tiny read-only worker (Haiku, budget $0.10) and prints its result block.

    Safe to re-run: it updates the skill and leaves your project files alone.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -ProjectPath C:\src\my-product -SmokeTest
#>
[CmdletBinding()]
param(
    [string] $ProjectPath,
    [ValidateSet('User', 'Project')] [string] $Scope = 'User',
    [switch] $SkipPermission,
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
if ($ProjectPath) {
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Container)) { Die "project path not found: $ProjectPath" }
    $ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
}

# ---------- 1. prerequisites ----------
Step 'Checking prerequisites'
$claude = Get-Command claude -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $claude) { Die "'claude' (Claude Code) is not on PATH" }
$help = (& $claude.Source --help 2>&1 | Out-String)
if ($help -notmatch '--bare') { Die "this Claude Code version has no --bare; update Claude Code" }
Ok ("claude: " + (& $claude.Source --version 2>&1 | Out-String).Trim())

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) { Die "'dotnet' is not on PATH; install the .NET SDK 8 or newer" }
$sdks = @(& $dotnet.Source --list-sdks 2>$null)
$okSdk = $sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 }
if (-not $okSdk) { Die ".NET SDK 8 or newer not found (dotnet --list-sdks: $($sdks -join '; '))" }
Ok ("dotnet SDK: " + (($okSdk | ForEach-Object { ($_ -split ' ')[0] }) -join ', '))

if ([string]::IsNullOrEmpty($env:ANTHROPIC_API_KEY)) {
    Ok "no ANTHROPIC_API_KEY: workers will run in lean mode on your Claude Code login (Pro/Max/Team/Enterprise subscription). Make sure 'claude' is logged in."
} else { Ok "ANTHROPIC_API_KEY is set: workers will run in bare mode (claude --bare)" }

# ---------- 2. copy the skill ----------
Step 'Installing the skill'
$userHome = $env:USERPROFILE
if (-not $userHome) { $userHome = $HOME }
if ($Scope -eq 'User') { $skillsRoot = Join-Path $userHome '.claude/skills' }
else { $skillsRoot = Join-Path $ProjectPath '.claude/skills' }
$target = Join-Path $skillsRoot 'lean-worker'
New-Item -ItemType Directory -Force -Path $target | Out-Null
# Replace the skill's own files but keep a previous build (bin/obj) to save time.
foreach ($item in @('SKILL.md', 'templates', 'launcher/Program.cs', 'launcher/LeanWorker.csproj')) {
    $from = Join-Path $source $item
    $to = Join-Path $target $item
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
    if (Test-Path -LiteralPath $from -PathType Container) {
        if (Test-Path -LiteralPath $to) { Remove-Item -LiteralPath $to -Recurse -Force }
        Copy-Item -LiteralPath $from -Destination $to -Recurse
    } else { Copy-Item -LiteralPath $from -Destination $to -Force }
}
Ok "skill installed at $target"

# ---------- 3. build the launcher ----------
Step 'Building the launcher'
$launcher = Join-Path $target 'launcher'
$buildOut = & $dotnet.Source build $launcher -c Release -v q -nologo 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Write-Host $buildOut; Die "launcher build failed" }
Ok "launcher built"
$launcherFwd = $launcher -replace '\\', '/'
$runCmd = "dotnet run --project `"$launcherFwd`" -c Release --"

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

    if (-not $SkipPermission) {
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
}

# ---------- 5. smoke test ----------
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
Write-Host "   1. Restart Claude Code (or start a new session) so it sees the skill."
if ($ProjectPath) {
    Write-Host "   2. In a session in $ProjectPath, ask:"
    Write-Host "        /lean-worker set up .lean-worker/project.md and profiles.json for this repository"
    Write-Host "      then review and edit both files by hand."
    Write-Host "   3. Delegate a task:  /lean-worker <what to do>; done when <command> passes"
} else {
    Write-Host "   2. Re-run with -ProjectPath <your repo> to prepare a project (templates, .gitignore, permission)."
}
