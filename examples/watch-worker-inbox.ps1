# Worker-side watcher: polls this agent's inbox cheaply; spawns the LLM agent ONLY when
# messages are waiting. All runs RESUME one persistent session (continuous agent memory)
# and stream their execution to logs\runs\run-*.jsonl (live view: viewer.ps1).
# Manual takeover on this machine: `claude --resume <id from agent-session-id.txt>`
# (disable the scheduled task while working manually).
#
# Fill in the four values below.
$ErrorActionPreference = 'Stop'
$agentName = '<AGENT_NAME>'                                   # e.g. clientx-d365
$dll       = '<REPO_PATH>\bin\Release\net8.0\AgentQueueMcp.dll'
$workspace = '<WORKSPACE_PATH>'                               # cwd for the agent (code + context files)
$dir       = '<STATE_PATH>'                                   # logs + lock + session id live here

$lock = Join-Path $dir 'watch.lock'
$sessionFile = Join-Path $dir 'agent-session-id.txt'
New-Item -ItemType Directory -Force (Join-Path $dir 'logs\runs') | Out-Null
function Log($m) { Add-Content (Join-Path $dir 'logs\watch.log') "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $m" }

# do not overlap a still-running pass (task analysis can take a long time)
if (Test-Path $lock) {
  if (((Get-Date) - (Get-Item $lock).LastWriteTime).TotalMinutes -lt 90) { exit 0 }
  Remove-Item $lock -Force   # stale lock = crashed pass
}

$env:AZURE_QUEUE_CONN = [Environment]::GetEnvironmentVariable('AZURE_QUEUE_CONN', 'User')
if (-not $env:AZURE_QUEUE_CONN) { Log 'AZURE_QUEUE_CONN missing'; exit 1 }
$env:AGENT_NAME = $agentName

# cheap poll: no LLM cost when the inbox is empty
$count = & dotnet $dll --peek
if ([int]$count -eq 0) { exit 0 }

Log "inbox: $count message(s) -> spawning agent"
New-Item -ItemType File $lock -Force | Out-Null
try {
  Set-Location $workspace
  $prompt = @'
Messages are waiting in this agent's inbox. Run the worker loop:
get_messages; for each task envelope process payload.brief in payload.mode
(read-only-analysis = analysis only, NO code changes), limited to payload.allowedTools where
applicable; when done send_result to the envelope sender with the envelope conversationId,
then ack_message. If blocked by a missing decision, send_message type=question to the sender
(same conversationId), note the context, and finish without blocking.
Messages of type info/progress: just note them. Never initiate new delegations.
'@
  $runLog = Join-Path $dir "logs\runs\run-$(Get-Date -Format 'yyyyMMdd-HHmmss').jsonl"
  $claudeArgs = @('-p', $prompt, '--output-format', 'stream-json', '--verbose', '--permission-mode', 'bypassPermissions')
  $sid = $null
  if (Test-Path $sessionFile) { $sid = (Get-Content $sessionFile -Raw -ErrorAction SilentlyContinue).Trim() }
  if ($sid) { $claudeArgs += @('--resume', $sid) }

  & claude @claudeArgs 1>$runLog 2>>(Join-Path $dir 'logs\watch.log')
  if ($LASTEXITCODE -ne 0 -and $sid) {
    Log "resume of session $sid failed (exit $LASTEXITCODE) - starting fresh session"
    Remove-Item $sessionFile -Force -ErrorAction SilentlyContinue
    $claudeArgs = $claudeArgs | Where-Object { $_ -notin @('--resume', $sid) }
    & claude @claudeArgs 1>$runLog 2>>(Join-Path $dir 'logs\watch.log')
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
