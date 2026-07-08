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
- **Manual takeover**: on the agent machine run `claude --resume <id from agent-session-id.txt>` —
  you get the full history of everything the automated agent did and can continue by hand in the
  same context. Disable the scheduled task first (`Disable-ScheduledTask <name>`) so the watcher
  does not drive the session in parallel; re-enable when done.

## Choosing intervals

- **Worker**: poll frequently (2–5 min) — tasks should start promptly.
- **Orchestrator**: conditional polling — frequent only while awaiting replies; a rare courtesy
  peek otherwise. The `pending` heuristic is just local files: `sent/<taskId>.json` without a
  matching `results/<taskId>.json` (which is why results must be archived under the payload
  `taskId`, not the envelope `messageId`).
