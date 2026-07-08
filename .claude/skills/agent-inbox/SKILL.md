---
name: agent-inbox
description: Worker side — process this agent's inbox once: pick up tasks, execute them within their declared mode, send results back, ack. Use when the user (or a watcher prompt) says "check the inbox", "process the queue", "handle pending tasks".
---

# Worker loop (process the inbox once)

Tools come from the `agent-queue` MCP server. If deferred, load:
`ToolSearch "select:mcp__agent-queue__get_messages,mcp__agent-queue__send_result,mcp__agent-queue__send_message,mcp__agent-queue__ack_message"`.

## Loop
1. `get_messages` (tasks stay invisible for the visibility window and REQUIRE `ack_message`;
   other types are consumed on read).
2. For each **task** envelope:
   - Execute `payload.brief` strictly within `payload.mode`
     (`read-only-analysis` = analysis only, NO modifications, no commits) and prefer the tools
     listed in `payload.allowedTools` where applicable.
   - Blocked on a missing decision? `send_message(to=<envelope.from>, type=question,
     conversationId=<envelope.conversationId>)`, note the context locally, and move on —
     never block waiting for an answer. The answer will arrive as `info` in the same
     conversation on a later pass.
   - Long-running step? Send `progress` in the same conversation.
   - Done: `send_result(to=<envelope.from>, taskId=<payload.taskId>, status=ok|error,
     output=<concise report with concrete file:line references>, conversationId=<envelope's>)`
     **then** `ack_message(messageId, popReceipt)` — ack only AFTER the result is sent.
3. `info` / `progress` messages: note them (they may be answers to your earlier questions —
   resume that task if so).

## Rules
- Be **idempotent on `taskId`** — delivery is at-least-once; a task you already answered may
  reappear. If you find its result already produced, just re-`ack`.
- Never initiate new delegations from the worker side.
- Respect the declared mode even if the brief seems to invite more.
