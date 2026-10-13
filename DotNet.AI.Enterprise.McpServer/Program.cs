using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Application.Rag;
using DotNet.AI.Enterprise.Infrastructure;
using DotNet.AI.Enterprise.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The HR core as an MCP server. A host (VS Code, Claude Desktop, MCP Inspector, another agent) starts this
// process and talks JSON-RPC over stdin/stdout. There is no LLM here: the host's model decides which tool
// to call, this process only runs it.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// stdout belongs to the protocol — one stray line breaks the JSON-RPC stream. All logs go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Same registrations as the console app and the Web API — only the presentation layer differs.
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "hr-assistant", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools(HrMcpTools.Create())
    .WithResources<PolicyResources>()
    .WithPrompts<HrPrompts>();

var host = builder.Build();

// Index the policy documents (only new or changed chunks are embedded) before accepting requests.
await host.Services.GetRequiredService<PolicyIndexer>().IndexAsync();

await host.RunAsync();
