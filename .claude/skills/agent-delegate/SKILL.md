---
name: agent-delegate
description: Orchestrator side — delegate a task to a named worker agent through the agent-queue MCP, answer worker questions, and collect results. Use when the user says "delegate <work> to <agent>", "send this to <agent>", "check results", "check the inbox", "any answers from the agents?". Delegation ONLY on an explicit user request — never automatically.
---

# Delegating work to agents (orchestrator)

Tools come from the `agent-queue` MCP server. If they are deferred, load them first:
`ToolSearch "select:mcp__agent-queue__send_task,mcp__agent-queue__get_messages,mcp__agent-queue__send_message,mcp__agent-queue__list_agents,mcp__agent-queue__peek_inbox"`.

## Delegate: "send <work> to <agent>"
1. No target given → `list_agents`, ask the user which one.
2. Compose a **work brief**: context, what exactly to analyse/do, expected output format,
   constraints (read-only unless the user explicitly says otherwise). Keep it under ~40 KB.
3. `send_task(to, title, brief, wiId?, project?)` — the server generates `taskId`
   (= `conversationId` of the whole thread) and archives a copy under `AGENT_COMM_DIR\sent\`.
4. Tell the user: taskId, target agent, one-line summary of what was sent.

## Collect: "check the inbox" / "check results"
1. `get_messages` — results/questions/progress are archived and consumed automatically.
2. For each `result`: match it to `sent\<taskId>.json`, summarize (status, key findings,
   file:line specifics if present). Point to the archived JSON for the full text.
3. For each `question`: show the FULL question to the user, wait for their answer, then
   `send_message(to=<envelope.from>, type=info, text=<answer>, conversationId=<from envelope>)`.
4. For `progress`/`info`: relay briefly.
5. A `task` addressed to THIS agent: do not execute automatically — show it to the user.

## Rules
- One work item = one task. Never invent delegations the user did not ask for.
- Do not echo secrets; the connection string lives in the environment only.
