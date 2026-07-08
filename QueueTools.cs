using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Storage.Queues;
using ModelContextProtocol.Server;

namespace AgentQueueMcp;

[McpServerToolType]
public static class QueueTools
{
    // Config (env):
    //   AZURE_QUEUE_CONN    required  Azure Storage connection string
    //   AGENT_NAME          required* own agent identity, e.g. "orchestrator", "klientx-d365"
    //                                 (*falls back to the machine name, with a stderr warning)
    //   AGENT_QUEUE_PREFIX  optional  inbox queue prefix (default: inbox)
    //   AGENT_COMM_DIR      optional  local archive dir (sent/, results/, inbox/)
    //
    // Model: every agent owns one queue "<prefix>-<agentName>" (its inbox) and
    // addresses peers by name. Envelope: { messageId, conversationId, from, to,
    // type: task|result|question|progress|info, payload, sentAt }.
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("AZURE_QUEUE_CONN")
        ?? throw new InvalidOperationException("AZURE_QUEUE_CONN is not set");
    private static readonly string Prefix =
        Sanitize(Environment.GetEnvironmentVariable("AGENT_QUEUE_PREFIX") is { Length: > 0 } p ? p : "inbox");
    public static readonly string AgentName = ResolveAgentName();
    private static readonly string? CommDir =
        Environment.GetEnvironmentVariable("AGENT_COMM_DIR") is { Length: > 0 } d ? d : null;

    private static readonly Lazy<QueueClient> OwnInbox = new(() => InboxFor(AgentName, create: true));

    private const int MaxMessageB64 = 60_000; // Azure hard limit is 64 KiB; leave headroom
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string ResolveAgentName()
    {
        var name = Environment.GetEnvironmentVariable("AGENT_NAME");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.MachineName;
            Console.Error.WriteLine($"AGENT_NAME not set — falling back to machine name '{name}'.");
        }
        return Sanitize(name);
    }

    // Azure queue names: 3-63 chars, lowercase alphanumeric + single hyphens.
    private static string Sanitize(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9-]", "-");
        s = Regex.Replace(s, "-{2,}", "-").Trim('-');
        if (s.Length < 1) throw new ArgumentException($"Invalid agent/queue name: '{name}'");
        return s;
    }

    private static QueueClient InboxFor(string agent, bool create)
    {
        var client = new QueueClient(Conn, $"{Prefix}-{Sanitize(agent)}");
        if (create) client.CreateIfNotExists();
        return client;
    }

    private static string Encode(object payload) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

    private static (JsonElement? Value, string? Raw) Decode(string messageText)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(messageText));
            return (JsonDocument.Parse(json).RootElement.Clone(), null);
        }
        catch
        {
            return (null, messageText);
        }
    }

    private static string? Archive(string subdir, string name, object payload)
    {
        if (CommDir is null) return null;
        var dir = Path.Combine(CommDir, subdir);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{name}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(payload, Pretty));
        return file;
    }

    private static string Out(object payload) => JsonSerializer.Serialize(payload, Pretty);

    private static async Task<(string ConversationId, object Envelope)> SendEnvelope(string to, string type, object payload, string? conversationId)
    {
        var convId = string.IsNullOrWhiteSpace(conversationId) ? Guid.NewGuid().ToString() : conversationId;
        var envelope = new
        {
            messageId = Guid.NewGuid().ToString(),
            conversationId = convId,
            from = AgentName,
            to = Sanitize(to),
            type,
            payload,
            sentAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        var body = Encode(envelope);
        if (body.Length > MaxMessageB64)
            throw new ArgumentException($"Message too large: {body.Length} B after base64 (limit {MaxMessageB64}). Shorten the payload.");
        await InboxFor(envelope.to, create: true).SendMessageAsync(body);
        return (convId, envelope);
    }

    // CLI helper for cheap polling from watch scripts: `dotnet AgentQueueMcp.dll --peek`.
    // Also stamps a heartbeat into the inbox metadata so agents_health can tell, from any
    // machine, whether this agent's watcher is alive.
    public static async Task<int> OwnInboxCount()
    {
        var props = await OwnInbox.Value.GetPropertiesAsync();
        await TouchHeartbeat();
        return props.Value.ApproximateMessagesCount;
    }

    private static async Task TouchHeartbeat()
    {
        try
        {
            await OwnInbox.Value.SetMetadataAsync(new Dictionary<string, string>
            {
                ["lastpeek"] = DateTimeOffset.UtcNow.ToString("o"),
            });
        }
        catch { /* heartbeat is best-effort */ }
    }

    [McpServerTool(Name = "send_task"),
     Description("Send a task to a named agent's inbox. Generates taskId (also used as conversationId unless given). Returns taskId and conversationId.")]
    public static async Task<string> SendTask(
        [Description("Target agent name, e.g. 'klientx-d365'")] string to,
        [Description("Short task title")] string title,
        [Description("Full work brief: context, what to analyse, expected output")] string brief,
        [Description("Related work item / ticket id")] int? wiId = null,
        [Description("Project name")] string? project = null,
        [Description("Tools the worker may use (default read-only set)")] string[]? allowedTools = null,
        [Description("Execution mode (default: read-only-analysis)")] string? mode = null,
        [Description("Existing conversation to attach to")] string? conversationId = null)
    {
        var task = new
        {
            taskId = Guid.NewGuid().ToString(),
            wiId,
            project,
            title,
            brief,
            allowedTools = allowedTools is { Length: > 0 } ? allowedTools : new[] { "Read", "Grep", "Glob" },
            mode = string.IsNullOrWhiteSpace(mode) ? "read-only-analysis" : mode,
            createdAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        var (convId, envelope) = await SendEnvelope(to, "task", task, conversationId ?? task.taskId);
        var archived = Archive("sent", task.taskId, envelope);
        return Out(new { sent = true, task.taskId, conversationId = convId, to = Sanitize(to), archived });
    }

    [McpServerTool(Name = "send_result"),
     Description("Send a task result back to the requesting agent (use the 'from' of the task envelope as 'to').")]
    public static async Task<string> SendResult(
        [Description("Target agent name (the task sender)")] string to,
        [Description("taskId from the task being answered")] string taskId,
        [Description("ok or error")] string status,
        [Description("Analysis result; on error may be partial")] string output,
        [Description("Related work item id")] int? wiId = null,
        [Description("Optional PR link")] string? prLink = null,
        [Description("Error description when status=error")] string? error = null,
        [Description("Conversation id from the task envelope")] string? conversationId = null)
    {
        if (status is not ("ok" or "error"))
            throw new ArgumentException("status must be 'ok' or 'error'");
        var result = new
        {
            taskId,
            wiId,
            status,
            output,
            prLink,
            error,
            finishedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        await SendEnvelope(to, "result", result, conversationId ?? taskId);
        return Out(new { sent = true, taskId, to = Sanitize(to) });
    }

    [McpServerTool(Name = "send_message"),
     Description("Send a free-form message to a named agent: type question (needs an answer), progress (status update), or info (answer / FYI). Use the conversationId of the thread you are replying to.")]
    public static async Task<string> SendMessage(
        [Description("Target agent name")] string to,
        [Description("question | progress | info")] string type,
        [Description("Message text")] string text,
        [Description("Conversation id of the thread (new one is generated if omitted)")] string? conversationId = null)
    {
        if (type is not ("question" or "progress" or "info"))
            throw new ArgumentException("type must be question, progress or info");
        var (convId, _) = await SendEnvelope(to, type, text, conversationId);
        return Out(new { sent = true, type, to = Sanitize(to), conversationId = convId });
    }

    [McpServerTool(Name = "get_messages"),
     Description("Read your own inbox. Non-task messages (result/question/progress/info) are archived and removed immediately. Tasks stay invisible for visibilityTimeoutSec and MUST be ack_message-d after processing, or they reappear (at-least-once — be idempotent on taskId).")]
    public static async Task<string> GetMessages(
        [Description("Max messages to collect, 1-32 (default 10)")] int max = 10,
        [Description("Invisibility window for received tasks, seconds 30-3600 (default 600)")] int visibilityTimeoutSec = 600)
    {
        max = Math.Clamp(max, 1, 32);
        visibilityTimeoutSec = Math.Clamp(visibilityTimeoutSec, 30, 3600);
        await TouchHeartbeat();
        var collected = new List<object>();
        while (collected.Count < max)
        {
            var batch = await OwnInbox.Value.ReceiveMessagesAsync(
                maxMessages: Math.Min(10, max - collected.Count),
                visibilityTimeout: TimeSpan.FromSeconds(visibilityTimeoutSec));
            if (batch.Value.Length == 0) break;
            foreach (var msg in batch.Value)
            {
                var (value, raw) = Decode(msg.MessageText);
                var type = value?.TryGetProperty("type", out var t) == true ? t.GetString() : null;
                if (value is null || type is null)
                {
                    // undecodable — archive raw and drop, never poison the inbox
                    var file = Archive("inbox", $"raw-{msg.MessageId}", new { rawText = raw ?? msg.MessageText });
                    await OwnInbox.Value.DeleteMessageAsync(msg.MessageId, msg.PopReceipt);
                    collected.Add(new { type = "undecodable", archived = file });
                    continue;
                }
                if (type == "task")
                {
                    collected.Add(new
                    {
                        envelope = (object)value,
                        messageId = msg.MessageId,
                        popReceipt = msg.PopReceipt,
                        dequeueCount = msg.DequeueCount,
                        ackRequired = true,
                    });
                }
                else
                {
                    string name = value.Value.TryGetProperty("messageId", out var mid) && mid.ValueKind == JsonValueKind.String
                        ? mid.GetString()! : msg.MessageId;
                    var subdir = type == "result" ? "results" : "inbox";
                    var file = Archive(subdir, name, value);
                    await OwnInbox.Value.DeleteMessageAsync(msg.MessageId, msg.PopReceipt);
                    collected.Add(new { envelope = (object)value, archived = file, ackRequired = false });
                }
            }
        }
        return Out(new { agent = AgentName, count = collected.Count, messages = collected });
    }

    [McpServerTool(Name = "ack_message"),
     Description("Delete a processed task message from your inbox. Call after the work is done and the result is sent.")]
    public static async Task<string> AckMessage(string messageId, string popReceipt)
    {
        await OwnInbox.Value.DeleteMessageAsync(messageId, popReceipt);
        return Out(new { acked = true, messageId });
    }

    [McpServerTool(Name = "peek_inbox"),
     Description("Non-destructive look at an inbox (own by default): approximate count and a preview of pending envelopes.")]
    public static async Task<string> PeekInbox(
        [Description("Agent name (default: own inbox)")] string? agent = null)
    {
        var client = agent is null ? OwnInbox.Value : InboxFor(agent, create: false);
        if (!await client.ExistsAsync())
            return Out(new { agent = agent ?? AgentName, exists = false });
        var props = await client.GetPropertiesAsync();
        var peeked = await client.PeekMessagesAsync(maxMessages: 10);
        var preview = peeked.Value.Select(m =>
        {
            var (value, _) = Decode(m.MessageText);
            if (value is null) return (object)new { undecodable = true };
            string? Get(string name) =>
                value.Value.TryGetProperty(name, out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null;
            return new { type = Get("type"), from = Get("from"), conversationId = Get("conversationId"), sentAt = Get("sentAt") };
        }).ToArray();
        return Out(new { agent = agent ?? AgentName, exists = true, approxCount = props.Value.ApproximateMessagesCount, preview });
    }

    [McpServerTool(Name = "list_agents"),
     Description("Discover agents: lists all inbox queues (agents that exist in this storage account) with message counts.")]
    public static async Task<string> ListAgents()
    {
        var service = new QueueServiceClient(Conn);
        var agents = new List<object>();
        await foreach (var q in service.GetQueuesAsync(prefix: $"{Prefix}-"))
        {
            var name = q.Name.Substring(Prefix.Length + 1);
            var props = await new QueueClient(Conn, q.Name).GetPropertiesAsync();
            agents.Add(new { agent = name, pending = props.Value.ApproximateMessagesCount, self = name == AgentName });
        }
        return Out(new { count = agents.Count, agents });
    }

    [McpServerTool(Name = "agents_health"),
     Description("Health of the whole agent mesh, from the queue's point of view: per agent — pending count, age of the oldest waiting message (growing backlog = that agent's watcher is not picking up), and time since its watcher's last peek heartbeat. Verdicts: ok | idle | watcher-stale | backlog.")]
    public static async Task<string> AgentsHealth()
    {
        var service = new QueueServiceClient(Conn);
        var now = DateTimeOffset.UtcNow;
        var agents = new List<object>();
        await foreach (var q in service.GetQueuesAsync(traits: Azure.Storage.Queues.Models.QueueTraits.Metadata, prefix: $"{Prefix}-"))
        {
            var name = q.Name.Substring(Prefix.Length + 1);
            var client = new QueueClient(Conn, q.Name);
            var props = await client.GetPropertiesAsync();
            var pending = props.Value.ApproximateMessagesCount;

            double? lastPeekMin = null;
            if (q.Metadata is not null && q.Metadata.TryGetValue("lastpeek", out var lp)
                && DateTimeOffset.TryParse(lp, out var lpTime))
            {
                lastPeekMin = Math.Round((now - lpTime).TotalMinutes, 1);
            }

            double? oldestMin = null;
            if (pending > 0)
            {
                var peeked = await client.PeekMessagesAsync(maxMessages: 1);
                var m = peeked.Value.FirstOrDefault();
                if (m?.InsertedOn is not null)
                    oldestMin = Math.Round((now - m.InsertedOn.Value).TotalMinutes, 1);
            }

            // verdict heuristics: watcher heartbeats every few minutes when configured
            string verdict;
            if (pending > 0 && oldestMin > 15) verdict = "backlog";           // messages waiting, nobody picks up
            else if (lastPeekMin is null) verdict = "watcher-unknown";        // never peeked (no watcher / manual only)
            else if (lastPeekMin > 60) verdict = "watcher-stale";             // watcher stopped heartbeating
            else if (pending > 0) verdict = "ok";                             // fresh work, watcher alive
            else verdict = "idle";                                            // empty inbox, watcher alive
            agents.Add(new
            {
                agent = name,
                self = name == AgentName,
                pending,
                oldestWaitingMin = oldestMin,
                lastWatcherPeekMin = lastPeekMin,
                verdict,
            });
        }
        return Out(new { checkedAt = now.ToString("o"), count = agents.Count, agents });
    }
}
