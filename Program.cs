// agent-queue-mcp (C#) — MCP server exposing Azure Storage Queues as
// agent-to-agent task delegation tools (pull model: both hosts poll, no inbound).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_QUEUE_CONN")))
{
    Console.Error.WriteLine("AZURE_QUEUE_CONN is not set. Refusing to start.");
    Environment.Exit(1);
}

// Cheap poll mode for watch scripts: prints pending message count of own inbox and exits.
if (args.Contains("--peek"))
{
    Console.WriteLine(await AgentQueueMcp.QueueTools.OwnInboxCount());
    return;
}

var builder = Host.CreateApplicationBuilder(args);
// stdout is the MCP transport — all logging must go to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
