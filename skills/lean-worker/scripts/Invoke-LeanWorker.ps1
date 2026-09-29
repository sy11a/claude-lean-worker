<#
.SYNOPSIS
    Runs one well-scoped task in a separate, minimal-context Claude Code process
    (`claude -p --bare`) and returns a compact report with token usage and cost.

.DESCRIPTION
    The orchestrating session writes a task file (and optionally a system file),
    then calls this script. The script:
      1. launches `claude -p` with only the context it is given explicitly,
      2. saves the raw stream to a run directory,
      3. computes usage (per API call, deduplicated by message id),
      4. appends one JSON line to <RunsRoot>/runs.jsonl,
      5. prints a short summary plus the worker's final report to stdout.

    Exit codes: 0 = worker finished without error, 1 = worker reported an error,
    2 = the launcher itself failed (bad arguments, claude not found, no API key).

    Works on Windows PowerShell 5.1 and PowerShell 7+. ASCII-only on purpose.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $TaskFile,
    [string]   $SystemFile,
    [string]   $Name,
    [string]   $Model = 'claude-sonnet-5',
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')] [string] $Effort = 'medium',
    [string[]] $Tools = @('Read', 'Edit', 'Write', 'Glob', 'Grep', 'Bash'),
    [string[]] $AllowedTools = @(),
    [string]   $McpConfig,
    [decimal]  $MaxBudgetUsd = 2,
    [ValidateSet('acceptEdits', 'dontAsk', 'plan', 'manual', 'auto', 'bypassPermissions')] [string] $PermissionMode = 'acceptEdits',
    [string]   $RunsRoot = '.lean-worker',
    [switch]   $ReplaceSystemPrompt,
    [switch]   $NoBare,
    [int]      $ReportMaxChars = 6000
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = $utf8
[Console]::OutputEncoding = $utf8

function Fail([string] $message) {
    Write-Output "LEAN-WORKER LAUNCH FAILED: $message"
    exit 2
}

function Get-Num($value) {
    if ($null -eq $value) { return [long]0 }
    return [long]$value
}

function Format-Int([long] $n) { return $n.ToString('N0', [Globalization.CultureInfo]::InvariantCulture) }

# ---------- validate inputs ----------
if (-not (Test-Path -LiteralPath $TaskFile -PathType Leaf)) { Fail "task file not found: $TaskFile" }
if ($SystemFile -and -not (Test-Path -LiteralPath $SystemFile -PathType Leaf)) { Fail "system file not found: $SystemFile" }
if ($McpConfig -and -not (Test-Path -LiteralPath $McpConfig -PathType Leaf)) { Fail "MCP config not found: $McpConfig" }

$claude = Get-Command claude -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $claude) { Fail "'claude' is not on PATH." }

if (-not $NoBare -and [string]::IsNullOrEmpty($env:ANTHROPIC_API_KEY)) {
    Fail "ANTHROPIC_API_KEY is not set. --bare reads only the API key (never OAuth/keychain). Set the key, or pass -NoBare to run the same lean profile without --bare."
}

# Default name: the folder that holds the task file (.lean-worker/inbox/<name>/task.md).
if (-not $Name) { $Name = Split-Path -Leaf (Split-Path -Parent (Resolve-Path -LiteralPath $TaskFile).Path) }
if (-not $Name) { $Name = 'task' }
$safeName = ($Name -replace '[^A-Za-z0-9._-]', '-')
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$runDir = Join-Path (Join-Path $RunsRoot 'runs') "$stamp-$safeName"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

Copy-Item -LiteralPath $TaskFile -Destination (Join-Path $runDir 'task.md')
if ($SystemFile) { Copy-Item -LiteralPath $SystemFile -Destination (Join-Path $runDir 'system.md') }

# ---------- build arguments ----------
$argList = New-Object System.Collections.Generic.List[string]
$argList.Add('-p')
if (-not $NoBare) { $argList.Add('--bare') }
$argList.AddRange([string[]]@('--model', $Model, '--effort', $Effort))
if ($SystemFile) {
    $flag = '--append-system-prompt-file'
    if ($ReplaceSystemPrompt) { $flag = '--system-prompt-file' }
    $argList.AddRange([string[]]@($flag, (Resolve-Path -LiteralPath $SystemFile).Path))
}
$argList.AddRange([string[]]@('--tools', ($Tools -join ',')))
if ($AllowedTools.Count -gt 0) {
    $argList.Add('--allowedTools')
    foreach ($t in $AllowedTools) { $argList.Add($t) }
}
$argList.Add('--strict-mcp-config')
if ($McpConfig) { $argList.AddRange([string[]]@('--mcp-config', (Resolve-Path -LiteralPath $McpConfig).Path)) }
$argList.AddRange([string[]]@(
    '--permission-mode', $PermissionMode,
    '--max-budget-usd', $MaxBudgetUsd.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--no-session-persistence',
    '--output-format', 'stream-json', '--verbose'))

[IO.File]::WriteAllText((Join-Path $runDir 'command.txt'), ('claude ' + ($argList -join ' ')), $utf8)

# ---------- run ----------
$streamPath = Join-Path $runDir 'stream.jsonl'
$stderrPath = Join-Path $runDir 'stderr.txt'
$taskText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $TaskFile).Path, $utf8)
$started = Get-Date
$lines = $taskText | & $claude.Source @argList 2> $stderrPath
$exitCode = $LASTEXITCODE
$elapsed = (Get-Date) - $started
if ($null -eq $lines) { $lines = @() }
[IO.File]::WriteAllLines($streamPath, [string[]]$lines, $utf8)

# ---------- parse ----------
$calls = New-Object System.Collections.Generic.List[object]
$seen = @{}
$result = $null
foreach ($line in $lines) {
    if (-not $line -or $line[0] -ne '{') { continue }
    try { $o = $line | ConvertFrom-Json } catch { continue }
    if ($o.type -eq 'assistant' -and $o.message -and $o.message.usage) {
        $id = [string]$o.message.id
        if ($seen.ContainsKey($id)) { continue }
        $seen[$id] = $true
        $u = $o.message.usage
        $ctx = (Get-Num $u.input_tokens) + (Get-Num $u.cache_read_input_tokens) + (Get-Num $u.cache_creation_input_tokens)
        $calls.Add([pscustomobject]@{ model = [string]$o.message.model; context = $ctx })
    }
    elseif ($o.type -eq 'result') { $result = $o }
}

$firstCtx = 0; $peakCtx = 0
if ($calls.Count -gt 0) {
    $firstCtx = $calls[0].context
    $peakCtx = [long](($calls | Measure-Object -Property context -Maximum).Maximum)
}

$status = 'no-result'
$report = ''
$usage = $null
if ($result) {
    $usage = $result.usage
    $report = [string]$result.result
    if ($result.is_error) { $status = 'error' } else { $status = 'success' }
}
elseif ($exitCode -ne 0) { $status = 'crashed' }

$tok = [ordered]@{
    input       = Get-Num $usage.input_tokens
    cache_write = Get-Num $usage.cache_creation_input_tokens
    cache_read  = Get-Num $usage.cache_read_input_tokens
    output      = Get-Num $usage.output_tokens
    thinking    = Get-Num $usage.output_tokens_details.thinking_tokens
}
$cost = 0.0
if ($result -and $null -ne $result.total_cost_usd) { $cost = [double]$result.total_cost_usd }
$denials = 0
if ($result -and $result.permission_denials) { $denials = @($result.permission_denials).Count }

$summary = [ordered]@{
    timestamp          = $started.ToString('o')
    name               = $Name
    run_dir            = $runDir
    model              = $Model
    effort             = $Effort
    bare               = (-not $NoBare)
    status             = $status
    subtype            = $(if ($result) { [string]$result.subtype } else { '' })
    terminal_reason    = $(if ($result) { [string]$result.terminal_reason } else { '' })
    exit_code          = $exitCode
    num_turns          = $(if ($result) { Get-Num $result.num_turns } else { 0 })
    api_calls          = $calls.Count
    duration_ms        = [long]$elapsed.TotalMilliseconds
    total_cost_usd     = $cost
    tokens             = $tok
    context_first_call = $firstCtx
    context_peak       = $peakCtx
    permission_denials = $denials
    session_id         = $(if ($result) { [string]$result.session_id } else { '' })
}
[IO.File]::WriteAllText((Join-Path $runDir 'summary.json'), ($summary | ConvertTo-Json -Depth 5), $utf8)
[IO.File]::WriteAllText((Join-Path $runDir 'report.md'), $report, $utf8)
$logLine = ($summary | ConvertTo-Json -Depth 5 -Compress) + [Environment]::NewLine
[IO.File]::AppendAllText((Join-Path $RunsRoot 'runs.jsonl'), $logLine, $utf8)

# ---------- print ----------
$mins = [int][Math]::Floor($elapsed.TotalMinutes)
$secs = $elapsed.Seconds
$costText = $cost.ToString('0.0000', [Globalization.CultureInfo]::InvariantCulture)
Write-Output 'LEAN-WORKER RESULT'
Write-Output ("run:      {0}" -f $runDir)
Write-Output ("status:   {0}  (subtype={1}, reason={2}, exit={3})" -f $status, $summary.subtype, $summary.terminal_reason, $exitCode)
Write-Output ("model:    {0}, effort {1}, bare={2}" -f $Model, $Effort, (-not $NoBare))
Write-Output ("work:     {0} turns, {1} API calls, {2}m{3:00}s" -f $summary.num_turns, $calls.Count, $mins, $secs)
Write-Output ("cost:     `${0} (list price reported by Claude Code)" -f $costText)
Write-Output ("tokens:   input {0} | cache write {1} | cache read {2} | output {3} (thinking {4})" -f (Format-Int $tok.input), (Format-Int $tok.cache_write), (Format-Int $tok.cache_read), (Format-Int $tok.output), (Format-Int $tok.thinking))
Write-Output ("context:  first call {0} | peak {1}" -f (Format-Int $firstCtx), (Format-Int $peakCtx))
if ($denials -gt 0) { Write-Output ("WARNING:  {0} permission denial(s); see stream.jsonl. Widen -AllowedTools if the task needed them." -f $denials) }
Write-Output '--- worker report ---'
if ($report.Length -gt $ReportMaxChars) {
    Write-Output $report.Substring(0, $ReportMaxChars)
    Write-Output ("[truncated; full report: {0}]" -f (Join-Path $runDir 'report.md'))
}
elseif ($report) { Write-Output $report }
else {
    Write-Output '(no report text)'
    if ((Test-Path -LiteralPath $stderrPath) -and (Get-Item -LiteralPath $stderrPath).Length -gt 0) {
        Write-Output '--- stderr (first 40 lines) ---'
        Get-Content -LiteralPath $stderrPath -TotalCount 40 | ForEach-Object { Write-Output $_ }
    }
}

if ($status -eq 'success') { exit 0 } else { exit 1 }
