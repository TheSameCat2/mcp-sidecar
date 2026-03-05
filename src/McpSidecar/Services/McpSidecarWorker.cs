using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpSidecar.Services;

/// <summary>
/// Main worker that orchestrates the MCP sidecar lifecycle.
/// </summary>
public class McpSidecarWorker : IHostedService
{
    private readonly ILogger<McpSidecarWorker> _logger;
    private readonly ClangdService _clangd;
    private readonly ExtractionService _extractionService;
    private readonly McpServer _mcpServer;
    private readonly string? _workspaceRoot;

    public McpSidecarWorker(
        ILogger<McpSidecarWorker> logger,
        ClangdService clangd,
        ExtractionService extractionService,
        McpServer mcpServer)
    {
        _logger = logger;
        _clangd = clangd;
        _extractionService = extractionService;
        _mcpServer = mcpServer;
        
        // Check for workspace root from environment or args
        _workspaceRoot = Environment.GetEnvironmentVariable("MCP_WORKSPACE_ROOT");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP Sidecar starting...");

        // Start clangd subprocess
        await _clangd.StartAsync(_workspaceRoot, cancellationToken);
        _logger.LogInformation("Clangd started successfully for workspace: {Workspace}", _clangd.WorkspaceRoot);

        // Start MCP server FIRST (so it can respond to requests)
        _ = _mcpServer.StartAsync(cancellationToken);
        _logger.LogInformation("MCP server listening on stdio");

        // Run extraction in background (don't block MCP server)
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshotId = await _extractionService.RunInitialExtractionAsync(cancellationToken);
                if (snapshotId.HasValue)
                {
                    _logger.LogInformation("Initial extraction finished with snapshot_id={SnapshotId}", snapshotId.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial extraction failed");
            }
        }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP Sidecar shutting down...");
        await _clangd.StopAsync(cancellationToken);
        _logger.LogInformation("Clangd stopped");
    }
}
