# Orchestrator-side watcher with CONDITIONAL polling:
#  - "pending" work exists (sent tasks without a result) -> peek on every pass,
#  - nothing pending -> courtesy peek at most every 30 min (catches unsolicited messages),
#  - the LLM agent is spawned ONLY when the peek finds messages.
# All runs RESUME one persistent session (continuous agent memory) and stream their
# execution to logs\runs\run-*.jsonl (live view: viewer.ps1).
# Manual takeover: `claude --resume <id from agent-session-id.txt>`
# (disable the scheduled task while working manually).
#
# Fill in the three values below. Convention: $comm is the archive dir passed to the MCP
# server as AGENT_COMM_DIR (sent/, results/, inbox/ live there; results are named
# <taskId>.json — that is what makes the pending detection work).
$ErrorActionPreference = 'Stop'
$dll  = '<REPO_PATH>\bin\Release\net8.0\AgentQueueMcp.dll'
$comm = '<COMM_PATH>'                                          # same dir as AGENT_COMM_DIR
$sdkCwd = '<ANY_DIR_WITH_@azure/storage-queue_INSTALLED>'      # used by the spawned agent

$lock = Join-Path $comm 'watch.lock'
$stamp = Join-Path $comm 'logs\lastpeek.txt'
$sessionFile = Join-Path $comm 'agent-session-id.txt'
New-Item -ItemType Directory -Force (Join-Path $comm 'logs\runs') | Out-Null
function Log($m) { Add-Content (Join-Path $comm 'logs\watch.log') "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $m" }

if (Test-Path $lock) {
  if (((Get-Date) - (Get-Item $lock).LastWriteTime).TotalMinutes -lt 30) { exit 0 }
  Remove-Item $lock -Force
}

# pending = sent\<taskId>.json without results\<taskId>.json
$pending = @(Get-ChildItem (Join-Path $comm 'sent\*.json') -ErrorAction SilentlyContinue | Where-Object {
  -not (Test-Path (Join-Path $comm "results\$($_.BaseName).json"))
})
if ($pending.Count -eq 0) {
  $last = if (Test-Path $stamp) { (Get-Item $stamp).LastWriteTime } else { [DateTime]::MinValue }
  if (((Get-Date) - $last).TotalMinutes -lt 30) { exit 0 }
}

$env:AZURE_QUEUE_CONN = [Environment]::GetEnvironmentVariable('AZURE_QUEUE_CONN', 'User')
if (-not $env:AZURE_QUEUE_CONN) { Log 'AZURE_QUEUE_CONN missing'; exit 1 }
$env:AGENT_NAME = 'orchestrator'

$count = & dotnet $dll --peek
New-Item -ItemType File $stamp -Force | Out-Null
if ([int]$count -eq 0) { exit 0 }

Log "pending=$($pending.Count), inbox: $count -> spawning agent"
New-Item -ItemType File $lock -Force | Out-Null
try {
  $prompt = @"
Messages arrived in the orchestrator inbox. Receive ALL of them (agent-queue MCP tools or the
storage SDK from $sdkCwd): decode each envelope, save results as $comm\results\<payload.taskId>.json
and other types as $comm\inbox\<messageId>.json, then delete from the queue. Do NOT delete or
execute type=task messages - only note them. Then PREPEND a concise summary of each message to
$comm\FEED.md: '## [date time] <type> from <agent> (conv: <first 8 chars>)' + 2-5 sentences
(for result: status + key findings; for question: FULL question text + 'AWAITING USER REPLY';
for progress/info: brief note). Send nothing, delegate nothing, answer nothing - the human does that.
FINALLY NOTIFY THE USER proactively (they want results pushed, not polled): if a push/desktop
notification tool is available in this session use it with a one-line summary; on Windows
additionally run: msg <username> "<one-line summary>" (no /time - the popup must stay until
the user closes it) so it reaches the desktop session even from session 0.
"@
  $runLog = Join-Path $comm "logs\runs\run-$(Get-Date -Format 'yyyyMMdd-HHmmss').jsonl"
  $claudeArgs = @('-p', $prompt, '--output-format', 'stream-json', '--verbose', '--permission-mode', 'bypassPermissions')
  $sid = $null
  if (Test-Path $sessionFile) { $sid = (Get-Content $sessionFile -Raw -ErrorAction SilentlyContinue).Trim() }
  if ($sid) { $claudeArgs += @('--resume', $sid) }

  & claude @claudeArgs 1>$runLog 2>>(Join-Path $comm 'logs\watch.log')
  if ($LASTEXITCODE -ne 0 -and $sid) {
    Log "resume of session $sid failed (exit $LASTEXITCODE) - starting fresh session"
    Remove-Item $sessionFile -Force -ErrorAction SilentlyContinue
    $claudeArgs = $claudeArgs | Where-Object { $_ -notin @('--resume', $sid) }
    & claude @claudeArgs 1>$runLog 2>>(Join-Path $comm 'logs\watch.log')
  }

  $resultLine = Get-Content $runLog -ErrorAction SilentlyContinue | Where-Object { $_ -match '"type"\s*:\s*"result"' } | Select-Object -Last 1
  if ($resultLine) {
    $r = $resultLine | ConvertFrom-Json
    if ($r.session_id) { Set-Content $sessionFile $r.session_id -Encoding ascii }
    $snippet = if ($r.result) { $r.result.Substring(0, [Math]::Min(300, $r.result.Length)) } else { '(no result)' }
    Log "agent [session $($r.session_id)]: $snippet"
  } else {
    Log "agent: no result event in $runLog (exit $LASTEXITCODE)"
  }
} finally {
  Remove-Item $lock -Force -ErrorAction SilentlyContinue
}
