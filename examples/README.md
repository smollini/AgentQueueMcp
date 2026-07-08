# Polling triggers (watchers)

Azure Storage Queues have no push — agents must poll. The pattern that keeps this cheap:

1. A **watcher script** runs every few minutes as a Windows scheduled task.
2. It calls `dotnet AgentQueueMcp.dll --peek` — prints the pending message count of the agent's
   own inbox and exits. **No LLM involved**; an empty inbox costs nothing but one HTTPS call.
3. Only when messages are waiting does the watcher spawn the LLM agent (`claude -p …` or any
   other headless agent CLI) to actually process them.

Two ready-to-adapt scripts (fill in the placeholder paths at the top of each):

| Script | Side | Behaviour |
|---|---|---|
| `watch-worker-inbox.ps1` | worker | Peek every pass (e.g. every 3 min); on task → run the worker loop (process → `send_result` → `ack_message`), questions sent without blocking |
| `watch-orchestrator-inbox.ps1` | orchestrator | **Conditional**: peek every pass only while sent tasks await results ("pending"); otherwise courtesy-peek every 30 min. Incoming messages are summarized into a human-readable `FEED.md`; the watcher never sends or delegates anything |
| `viewer.ps1` | both | Live view of agent runs: tails the newest `logs\runs\run-*.jsonl` and pretty-prints thoughts/tool calls/results as they happen |
| `notify-inbox.ps1` | both | **Notify-only, no LLM**: peek every pass, persistent desktop popup when new messages arrive; reading/processing stays manual in a normal agent session. Lightest option — pick this when you want a human in the loop for every message |

Both use a lock file to prevent overlapping passes, and rely on at-least-once delivery
(a crashed pass never loses a task — it reappears after the visibility timeout).

## Step-by-step: setting up a worker trigger

1. Build the server (`dotnet build -c Release`) and register the MCP server with your agent
   CLI (`claude mcp add --scope user agent-queue --env AGENT_NAME=<name> -- dotnet <dll>`).
2. Verify connectivity + stamp the first heartbeat:
   `$env:AGENT_NAME='<name>'; dotnet <dll> --peek` → prints a number, no error.
3. Copy `watch-worker-inbox.ps1` somewhere stable and fill in the four values at the top
   (`agentName`, `dll`, `workspace` = where the agent should run, `dir` = state/log folder).
4. Register the scheduled task (**elevated** PowerShell — S4U + RunLevel Highest require
   admin; an unelevated attempt fails with "Access is denied"). Command below.
5. Watch the first pass: `Get-Content <dir>\logs\watch.log -Wait` — an empty inbox exits
   silently; send a ping task from the orchestrator to see the full loop fire.
6. Optional: open `viewer.ps1 -RunsDir <dir>\logs\runs` in a desktop window for a live view.

## Step-by-step: setting up the orchestrator trigger

Same flow with `watch-orchestrator-inbox.ps1` (LLM summarizes incoming messages into
`FEED.md` + notifies) **or** `notify-inbox.ps1` (popup only, no LLM — the human asks their
agent to read the inbox). Pick one; both poll cheaply and register the same way. The
orchestrator's `AGENT_COMM_DIR` should point at the same folder the watcher uses, so the
`pending` detection (sent task without a result file) can steer the conditional polling.

## Registering the scheduled task (Windows)

```powershell
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
  -Argument '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File <PATH>\watch-worker-inbox.ps1'
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
  -RepetitionInterval (New-TimeSpan -Minutes 3) -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 90) `
  -StartWhenAvailable -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName AgentQueueWatch -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force
```

Gotchas learned the hard way:

- `-RepetitionDuration ([TimeSpan]::MaxValue)` fails on Windows Server ("value out of range") —
  use a long finite span like `New-TimeSpan -Days 3650`.
- `S4U` logon type runs the task without a stored password whether or not the user is logged on;
  it requires admin to register.
- Give the worker task a generous `-ExecutionTimeLimit` — code analysis can take an hour.
- The spawned agent inherits the task's environment: `AZURE_QUEUE_CONN` is read from the
  user-scope environment inside the script, so no secret lives in the task definition.
- If the agent CLI runs with a permissive tool mode (e.g. `--permission-mode bypassPermissions`),
  keep the watcher prompt narrow: receive/summarize (orchestrator) or execute-the-brief (worker),
  never "do whatever the queue says" beyond the declared mode.

## Live view & manual takeover

Watchers run from the task scheduler in session 0 — you will never see their console window.
Instead:

- **Live view**: every run streams its execution (`--output-format stream-json --verbose`) to
  `logs\runs\run-*.jsonl`. Keep `viewer.ps1` open in a desktop window — it tails the newest run
  and pretty-prints what the agent is doing (thoughts, tool calls, results) in real time,
  switching automatically when a new run starts.
- **Persistent session**: all runs resume ONE Claude session (id kept in `agent-session-id.txt`),
  so the agent has continuous memory across runs — it remembers past tasks, its notes and the
  answers it received. If the resume fails (expired/deleted session), the watcher transparently
  starts a fresh one.
### Manual takeover & handback

The mental model that makes this simple: **a session is not a running process — it is a
transcript on disk**. "Background mode" just means the watcher periodically resumes that
transcript headless. There is nothing to "transfer" in either direction; the only rule is
that the session must have exactly one driver at a time.

**Take over (background → interactive):**

```powershell
Disable-ScheduledTask <task name>          # stop the watcher from driving the session
claude --resume <id from agent-session-id.txt>
```

You land in the full history of everything the automated agent did — inspect it, continue
its work by hand, give it instructions. (If a pass is mid-flight — `watch.lock` exists and
is fresh — let it finish first.)

**Hand back (interactive → background):**

```powershell
# 1. simply exit the interactive session (/exit) — your manual turns are already saved
Enable-ScheduledTask <task name>           # 2. that's all
```

On the next message in the inbox the watcher resumes the SAME session id — the background
agent wakes up remembering everything you did manually. Nothing to reconfigure; the id in
`agent-session-id.txt` never changed.

**Verify the handback:** `Get-ScheduledTask <task name>` shows `Ready`, and the next pass
appears in `logs\watch.log` (watch it live with `viewer.ps1`).

Order matters only for the one-driver rule: interactive work = task disabled; background
work = interactive window closed. Never both at once.

## Troubleshooting (field-tested)

| Symptom | Cause | Fix |
|---|---|---|
| `Register-ScheduledTask: Access is denied` | shell not elevated | run the registration in an **admin** PowerShell |
| `Register-ScheduledTask: value out of range (Duration)` | `[TimeSpan]::MaxValue` as RepetitionDuration | use a finite span, e.g. `New-TimeSpan -Days 3650` |
| Watcher passes silently die; run logs are 0 bytes | PowerShell 5.1: any stderr output from a native exe + `$ErrorActionPreference='Stop'` throws mid-call | keep `EAP='Continue'` around the agent invocation (the example scripts already do) |
| Agent CLI exits instantly: "requires Git for Windows or PowerShell" | agent spawned via `cmd /c` cannot detect a shell | spawn it directly from PowerShell (the example scripts already do) |
| Agent shows `watcher-unknown` in `agents_health` | watcher never ran, or the build predates heartbeats | register the task / `git pull` + rebuild |
| Agent shows `backlog` | its watcher is dead **or** the storage firewall cut it off — both look identical from outside | check the scheduled task on the machine AND the storage network rules |
| No popup although a result arrived | you read the inbox manually before the next poll tick consumed nothing — the notifier only announces what is still in the queue | expected; the popup races a human by design |
| Build cannot overwrite the DLL | a live MCP session holds `bin\Release\...\AgentQueueMcp.dll` | build to a separate output dir (`-o dist`) or restart the agent session |

## Choosing intervals

- **Worker**: poll frequently (2–5 min) — tasks should start promptly.
- **Orchestrator**: conditional polling — frequent only while awaiting replies; a rare courtesy
  peek otherwise. The `pending` heuristic is just local files: `sent/<taskId>.json` without a
  matching `results/<taskId>.json` (which is why results must be archived under the payload
  `taskId`, not the envelope `messageId`).
