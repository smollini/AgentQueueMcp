# NOTIFY-ONLY watcher (no LLM): peek the inbox every pass; when NEW messages appear, show a
# persistent desktop popup and stop. Reading/processing stays manual (ask your agent in a
# normal session to check the inbox). Lightest possible automation — good when you want to
# stay in the loop for every message.
#
# Dependency-free: uses only `dotnet AgentQueueMcp.dll --peek` (which also stamps the
# heartbeat for agents_health). Dedup is count-based: you get a popup when the number of
# waiting messages grows; the counter resets when the inbox is emptied.
#
# Register as a scheduled task every 2-5 min (see README.md in this folder).
# Fill in the three values below.
$ErrorActionPreference = 'Continue'
$agentName = '<AGENT_NAME>'
$dll       = '<REPO_PATH>\bin\Release\net8.0\AgentQueueMcp.dll'
$dir       = '<STATE_PATH>'                    # state + log live here
$notifyUser = $env:USERNAME                    # who gets the msg.exe popup

$state = Join-Path $dir 'notified-count.txt'
New-Item -ItemType Directory -Force $dir | Out-Null
function Log($m) { Add-Content (Join-Path $dir 'notify.log') "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $m" }

$env:AZURE_QUEUE_CONN = [Environment]::GetEnvironmentVariable('AZURE_QUEUE_CONN', 'User')
if (-not $env:AZURE_QUEUE_CONN) { Log 'AZURE_QUEUE_CONN missing'; exit 1 }
$env:AGENT_NAME = $agentName

$count = [int](& dotnet $dll --peek)
$last = 0
if (Test-Path $state) { $last = [int](Get-Content $state) }

if ($count -eq 0) {
  if ($last -ne 0) { Set-Content $state 0 }
  exit 0
}
if ($count -le $last) { exit 0 }   # nothing new since the last popup

$text = "Agent queue: $count message(s) waiting in inbox-$agentName. Ask your agent to check the inbox."
Log "popup: $text"
msg $notifyUser $text              # no /time: stays until dismissed
Set-Content $state $count
