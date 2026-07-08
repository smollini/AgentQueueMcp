// End-to-end test: TWO agents (orchestrator + worker), each with its own server
// instance and inbox, full two-way conversation over real MCP stdio transport.
// Throwaway queue prefix; queues are deleted at the end.
// Usage: cd test && npm install && node e2e.mjs   (requires AZURE_QUEUE_CONN in env)
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const dll = path.join(root, 'bin', 'Release', 'net8.0', 'AgentQueueMcp.dll');
const dotnet = process.env.DOTNET_BIN || 'dotnet';
const PREFIX = 'e2ebox';

async function spawnAgent(name) {
  const transport = new StdioClientTransport({
    command: dotnet,
    args: [dll],
    env: { ...process.env, AGENT_NAME: name, AGENT_QUEUE_PREFIX: PREFIX, AGENT_COMM_DIR: '' },
  });
  const client = new Client({ name: `e2e-${name}`, version: '0.0.0' });
  await client.connect(transport);
  const call = async (tool, args = {}) => {
    const res = await client.callTool({ name: tool, arguments: args });
    if (res.isError) throw new Error(`${name}/${tool} failed: ${res.content[0]?.text}`);
    const out = JSON.parse(res.content[0].text);
    console.log(`\n== [${name}] ${tool} ==\n`, JSON.stringify(out));
    return out;
  };
  return { client, call };
}

const orch = await spawnAgent('e2e-orch');
const worker = await spawnAgent('e2e-worker');

// 1. orchestrator -> task -> worker
const sent = await orch.call('send_task', {
  to: 'e2e-worker',
  title: 'two-way e2e',
  brief: 'Test addressed delegation with a follow-up question.',
  wiId: 0,
});

// 2. worker receives the task (ack required)
const inbox1 = await worker.call('get_messages', {});
const taskMsg = inbox1.messages.find((m) => m.envelope?.type === 'task');
if (!taskMsg || taskMsg.envelope.payload.taskId !== sent.taskId) throw new Error('worker did not receive the task');
if (!taskMsg.ackRequired) throw new Error('task should require ack');

// 3. worker asks a question in the same conversation
await worker.call('send_message', {
  to: taskMsg.envelope.from,
  type: 'question',
  text: 'Which invoice layout should I check?',
  conversationId: taskMsg.envelope.conversationId,
});

// 4. orchestrator sees the question (auto-acked) and answers
const inbox2 = await orch.call('get_messages', {});
const q = inbox2.messages.find((m) => m.envelope?.type === 'question');
if (!q || q.envelope.conversationId !== sent.conversationId) throw new Error('orchestrator did not receive the question');
if (q.ackRequired) throw new Error('question should be auto-acked');
await orch.call('send_message', {
  to: q.envelope.from,
  type: 'info',
  text: 'Check the default layout.',
  conversationId: q.envelope.conversationId,
});

// 5. worker reads the answer
const inbox3 = await worker.call('get_messages', {});
const a = inbox3.messages.find((m) => m.envelope?.type === 'info');
if (!a || a.envelope.payload !== 'Check the default layout.') throw new Error('worker did not receive the answer');

// 6. worker sends the result and acks the task
await worker.call('send_result', {
  to: taskMsg.envelope.from,
  taskId: sent.taskId,
  status: 'ok',
  output: 'two-way e2e result payload',
  conversationId: taskMsg.envelope.conversationId,
});
await worker.call('ack_message', { messageId: taskMsg.messageId, popReceipt: taskMsg.popReceipt });

// 7. orchestrator collects the result
const inbox4 = await orch.call('get_messages', {});
const r = inbox4.messages.find((m) => m.envelope?.type === 'result');
if (!r || r.envelope.payload.taskId !== sent.taskId || r.envelope.payload.output !== 'two-way e2e result payload')
  throw new Error('orchestrator did not receive the result');

// 8. discovery
const agents = await orch.call('list_agents', {});
for (const expected of ['e2e-orch', 'e2e-worker'])
  if (!agents.agents.some((x) => x.agent === expected)) throw new Error(`list_agents missing ${expected}`);

await orch.client.close();
await worker.client.close();

const { QueueClient } = await import('@azure/storage-queue');
await new QueueClient(process.env.AZURE_QUEUE_CONN, `${PREFIX}-e2e-orch`).deleteIfExists();
await new QueueClient(process.env.AZURE_QUEUE_CONN, `${PREFIX}-e2e-worker`).deleteIfExists();
console.log('\nE2E OK: addressed two-way conversation passed, throwaway queues removed.');
process.exit(0);
