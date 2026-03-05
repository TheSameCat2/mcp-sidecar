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

// Register services
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<ISqlBuilder, SqlBuilder>();
builder.Services.AddSingleton<ClangdService>();
builder.Services.AddSingleton<ExtractionService>();
builder.Services.AddSingleton<McpServer>();
builder.Services.AddHostedService<McpSidecarWorker>();

var host = builder.Build();
await host.RunAsync();
