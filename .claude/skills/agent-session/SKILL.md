---
name: agent-session
description: Manage the background agent session driven by the watchers — inspect it, take it over interactively, hand it back so the watchers keep running. Use when the user says "take over the agent session", "resume the background session", "hand the session back", "what has the background agent been doing".
---

# Background agent session (takeover & handback)

The watcher scripts (see `examples/`) run the LLM agent headless and **resume one persistent
session** every pass — its id lives in `<STATE_PATH>\agent-session-id.txt`. That session is a
normal Claude Code session: it can be opened interactively with full history, worked in by a
human, and then handed back to the watchers.

## Inspect: "what has the background agent been doing"
- Session id: read `agent-session-id.txt`.
- Recent activity: newest `logs\runs\run-*.jsonl` (stream of thoughts/tool calls) — or keep
  `examples/viewer.ps1` open in a desktop window for a live pretty-printed view.
- Watcher decisions: `logs\watch.log`.

## Take over: "resume the background session"
1. Pause the trigger so the watcher does not drive the session in parallel:
   `Disable-ScheduledTask <task name>` (and delete a fresh `watch.lock` only if you are sure
   no pass is mid-flight).
2. `claude --resume <id from agent-session-id.txt>` — full history of everything the
   automated agent did, continue by hand in the same context.

## Hand back: "resume the watchers"
1. Exit the interactive session.
2. `Enable-ScheduledTask <task name>` — the next pass resumes the SAME session id; the
   background agent remembers whatever you did manually.

## Notes
- If the session was deleted/expired, watchers transparently start a fresh one and update
  `agent-session-id.txt` — nothing to repair by hand.
- One session per agent role. Do not resume the same session from two places at once.
- If context has grown huge, it is fine to delete `agent-session-id.txt` — the next pass
  starts clean (history stays browsable via `claude --resume` picker).
