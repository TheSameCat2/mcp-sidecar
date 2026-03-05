using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using McpSidecar.Services.Database;
using McpSidecar.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging - send to stderr only (stdout is for MCP protocol)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => 
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Determine workspace root: CLI arg > env var > current directory
var workspaceRoot = args.SkipWhile(a => a != "--workspace" && a != "-w")
                        .Skip(1)
                        .FirstOrDefault()
                   ?? Environment.GetEnvironmentVariable("MCP_WORKSPACE_ROOT")
                   ?? Directory.GetCurrentDirectory();

// Normalize to absolute path
workspaceRoot = Path.GetFullPath(workspaceRoot);

// Register workspace root as a singleton
builder.Services.AddSingleton(new WorkspaceOptions { Root = workspaceRoot });

// Register services
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<ISqlBuilder, SqlBuilder>();
builder.Services.AddSingleton<ClangdService>();
builder.Services.AddSingleton<ExtractionService>();
builder.Services.AddSingleton<McpServer>();
builder.Services.AddHostedService<McpSidecarWorker>();

var host = builder.Build();
await host.RunAsync();

// Simple options class for workspace configuration
public class WorkspaceOptions
{
    public string Root { get; set; } = string.Empty;
}
