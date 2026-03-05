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

        // Run extraction in background - track the task so we can wait for it on shutdown
        _extractionTask = Task.Run(async () =>
        {
            try
            {
                // Use a separate cancellation token that we control
                var snapshotId = await _extractionService.RunInitialExtractionAsync(_extractionCts.Token);
                if (snapshotId.HasValue)
                {
                    _logger.LogInformation("Initial extraction finished with snapshot_id={SnapshotId}", snapshotId.Value);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Initial extraction cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial extraction failed");
            }
        }, CancellationToken.None); // Don't use hosted service token
    }

    private readonly CancellationTokenSource _extractionCts = new();
    private Task? _extractionTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP Sidecar shutting down...");
        
        // Wait for extraction to complete (with timeout)
        if (_extractionTask != null && !_extractionTask.IsCompleted)
        {
            _logger.LogInformation("Waiting for extraction to complete...");
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            var completedTask = await Task.WhenAny(_extractionTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("Extraction timed out, cancelling...");
                _extractionCts.Cancel();
            }
        }
        
        await _clangd.StopAsync(cancellationToken);
        _logger.LogInformation("Clangd stopped");
    }
}
