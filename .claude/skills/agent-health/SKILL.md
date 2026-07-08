---
name: agent-health
description: Check the health of the whole agent mesh and of local triggers — are watchers alive, is anything stuck in queues, is a worker not picking up. Use when the user asks "are the watchers running", "trigger status", "agent health", "why is nothing coming back".
---

# Agent mesh & trigger health

## Layer 1 — the queues (every agent, from anywhere)
`ToolSearch "select:mcp__agent-queue__agents_health"` → `agents_health`.

Verdict interpretation:
- `idle` — empty inbox, watcher heartbeating (last peek < 60 min): healthy.
- `ok` — messages waiting, watcher alive: they are about to be picked up.
- `backlog` — messages older than 15 min: **that agent's watcher is not picking up**
  (scheduled task dead, machine off, old build, or the storage firewall is blocking it —
  a firewalled-out worker looks IDENTICAL to a dead one; check network rules before
  debugging the machine).
- `watcher-stale` — heartbeat older than 60 min: watcher stopped (inbox may still be empty).
- `watcher-unknown` — never heartbeated: watcher not installed, or a build older than the
  heartbeat feature.

## Layer 2 — this host's triggers (local)
- Scheduled task: `Get-ScheduledTask <name>; Get-ScheduledTaskInfo <name>` —
  expect `State=Ready`, `LastTaskResult=0` (267009 = currently running; 267014 = terminated).
- Watcher log freshness (`logs\watch.log` / `notify.log`): entries should be recent relative
  to the polling interval.
- Stale `watch.lock` (older than the run limit): a crashed pass — safe to delete the file.
- Pending sanity: entries in `sent\` without a matching `results\<taskId>.json` are awaiting
  replies; if one is hours old AND the target's inbox is empty, the worker took the task and
  died mid-run — the task will reappear after its visibility timeout (check `dequeueCount`).

## Report
Summarize per agent in one short table + a one-sentence verdict ("all good" / "worker X has
not picked up for Y min — its scheduled task or network path is down"). Read-only — change
nothing while diagnosing.
